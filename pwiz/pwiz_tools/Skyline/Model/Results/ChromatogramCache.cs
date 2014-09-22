/*
 * Original author: Brendan MacLean <brendanx .at. u.washington.edu>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2009 University of Washington - Seattle, WA
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using pwiz.Common.Collections;
using pwiz.Common.SystemUtil;
using pwiz.Skyline.Properties;
using pwiz.Skyline.Util;
using pwiz.Skyline.Util.Extensions;

namespace pwiz.Skyline.Model.Results
{
    public sealed class ChromatogramCache : Immutable, IDisposable
    {
        public const int FORMAT_VERSION_CACHE_9 = 9; // Introduces abbreviated scan ids
        public const int FORMAT_VERSION_CACHE_8 = 8; // Introduces ion mobility data
        public const int FORMAT_VERSION_CACHE_7 = 7; // Introduces UTF8 character support
        public const int FORMAT_VERSION_CACHE_6 = 6;
        public const int FORMAT_VERSION_CACHE_5 = 5;
        public const int FORMAT_VERSION_CACHE_4 = 4;
        public const int FORMAT_VERSION_CACHE_3 = 3;
        public const int FORMAT_VERSION_CACHE_2 = 2;

        public const string EXT = ".skyd"; // Not L10N
        public const string PEAKS_EXT = ".peaks"; // Not L10N
        public const string SCANS_EXT = ".scans"; // Not L10N

        public static int FORMAT_VERSION_CACHE
        {
            get { return FORMAT_VERSION_CACHE_9; }
        }

        /// <summary>
        /// Construct path to a final data cache from the document path.
        /// </summary>
        /// <param name="documentPath">Path to saved document</param>
        /// <param name="name">Name of data cache</param>
        /// <returns>A path to the data cache</returns>
        public static string FinalPathForName(string documentPath, string name)
        {
            string documentDir = Path.GetDirectoryName(documentPath) ?? string.Empty;
            string modifier = (name != null ? '_' + name : string.Empty); // Not L10N
            return Path.Combine(documentDir,
                Path.GetFileNameWithoutExtension(documentPath) + modifier + EXT);
        }

        /// <summary>
        /// Construct path to a part of a progressive data cache creation
        /// in the document directory, named after the result file.
        /// </summary>
        /// <param name="documentPath">Path to saved document</param>
        /// <param name="dataFilePath">Results file path</param>
        /// <param name="name">Name of data cache</param>
        /// <returns>A path to the data cache</returns>
        public static string PartPathForName(string documentPath, MsDataFileUri dataFilePath, string name = null)
        {
            string dirDocument = Path.GetDirectoryName(documentPath) ?? string.Empty;

            // Start with the file basename
            StringBuilder sbName = new StringBuilder(dataFilePath.GetFileNameWithoutExtension());
            // If the data file is not in the same directory as the document, add a checksum
            // of the data directory.
            var msDataFilePath = dataFilePath as MsDataFilePath;
            if (msDataFilePath != null)
            {
                string dirData = Path.GetDirectoryName(msDataFilePath.FilePath);
                // Perhaps one of these hasn't a path at all - are both in the current working directory?
                string fullDocDirPath = String.IsNullOrEmpty(dirDocument) ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(Path.GetFullPath(dirDocument));
                string fullFileDirPath = String.IsNullOrEmpty(dirData) ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(Path.GetFullPath(dirData));
                if (!Equals(fullDocDirPath, fullFileDirPath))
                    sbName.Append('_').Append(AdlerChecksum.MakeForString(fullFileDirPath));
            }
            // If it has a sample name, append the index to differentiate this name from
            // the other samples in the multi-sample file
            if (null != dataFilePath.GetSampleName())
                sbName.Append('_').Append(dataFilePath.GetSampleIndex());
            if (name != null)
                sbName.Append('_').Append(name);
            // Append the extension to differentiate between different file types (.mzML, .mzXML)
            sbName.Append(dataFilePath.GetExtension());
            sbName.Append(EXT);

            return Path.Combine(dirDocument, sbName.ToString());
        }

        private readonly ReadOnlyCollection<ChromCachedFile> _cachedFiles;
        // ReadOnlyCollection is not fast enough for use with these arrays
        private readonly ChromGroupHeaderInfo5[] _chromatogramEntries;
        private readonly ChromTransition[] _chromTransitions;
        private readonly BlockedArray<ChromPeak> _chromatogramPeaks;
        private readonly Dictionary<Type, int> _scoreTypeIndices;
        private readonly float[] _scores;
        private readonly byte[] _seqBytes;
        private readonly long _locationScanIds;
        private readonly long _countBytesScanIds;

        public ChromatogramCache(string cachePath, RawData raw, IPooledStream readStream)
        {
            CachePath = cachePath;
            Version = raw.FormatVersion;
            _cachedFiles = MakeReadOnly(raw.ChromCacheFiles);
            _chromatogramEntries = raw.ChromatogramEntries;
            _chromTransitions = raw.ChromTransitions;
            _chromatogramPeaks = raw.ChromatogramPeaks;
            _scoreTypeIndices = new Dictionary<Type, int>();
            for (int i = 0; i < raw.ScoreTypes.Length; i++)
                _scoreTypeIndices.Add(raw.ScoreTypes[i], i);
            _scores = raw.Scores;
            _seqBytes = raw.SeqBytes;
            _locationScanIds = raw.LocationScanIds;
            _countBytesScanIds = raw.CountBytesScanIds;
            ReadStream = readStream;
        }

        public string CachePath { get; private set; }
        public int Version { get; private set; }
        public IList<ChromCachedFile> CachedFiles { get { return _cachedFiles; } }
        public IPooledStream ReadStream { get; private set; }

        public IEnumerable<MsDataFileUri> CachedFilePaths
        {
            get { return CachedFiles.Select(cachedFile => cachedFile.FilePath); }
        }

        /// <summary>
        /// In order enumeration of score types
        /// </summary>
        public IEnumerable<Type> ScoreTypes
        {
            get { return _scoreTypeIndices.OrderBy(p => p.Value).Select(p => p.Key); }
        }

        /// <summary>
        /// True if cache version is acceptable for current use.
        /// </summary>
        public bool IsSupportedVersion
        {
            get { return (Version >= FORMAT_VERSION_CACHE_2); }
        }

        public bool IsCurrentVersion
        {
            get { return IsVersionCurrent(Version); }
        }

        public static bool IsVersionCurrent(int version)
        {
            return (version >= FORMAT_VERSION_CACHE_3 && FORMAT_VERSION_CACHE >= version);
        }

        public bool IsCurrentDisk
        {
            get { return CachedFiles.IndexOf(cachedFile => !cachedFile.IsCurrent) == -1; }
        }

        public ChromTransition GetTransition(int index)
        {
            return _chromTransitions[index];
        }

        public ChromPeak GetPeak(int index)
        {
            return _chromatogramPeaks[index];
        }

        public IEnumerable<float> GetCachedScores(int index)
        {
            return _scores.Skip(index).Take(_scoreTypeIndices.Count);
        }

        /// <summary>
        /// Returns true if the cached file paths in this cache are completely covered
        /// by an existing set of caches.
        /// </summary>
        /// <param name="caches">Existing caches to check for paths in this cache that are missing</param>
        /// <returns>True if all paths in this cache are covered</returns>
        public bool IsCovered(IEnumerable<ChromatogramCache> caches)
        {
            // True if there are not any paths that are not covered
            return CachedFilePaths.All(path => IsCovered(path, caches));
        }

        /// <summary>
        /// Returns true, if a single path can be found in a set of caches.
        /// </summary>
        private static bool IsCovered(MsDataFileUri path, IEnumerable<ChromatogramCache> caches)
        {
            return caches.Any(cache => cache.CachedFilePaths.Contains(path));
        }

        public DataFileScanIds LoadScanIds(int fileIndex)
        {
            return DataFileScanIds.FromBytes(LoadScanIdBytes(fileIndex));
        }

        public byte[] LoadScanIdBytes(int fileIndex)
        {
            var cachedFile = CachedFiles[fileIndex];
            byte[] scanIdBytes = new byte[cachedFile.SizeScanIds];
            if (scanIdBytes.Length > 0)
            {
                Stream stream = ReadStream.Stream;
                lock (stream)
                {
                    stream.Seek(_locationScanIds + cachedFile.LocationScanIds, SeekOrigin.Begin);

                    // Single read to get all the points
                    if (stream.Read(scanIdBytes, 0, scanIdBytes.Length) < scanIdBytes.Length)
                        throw new IOException(Resources.ChromatogramCache_LoadScanIdBytes_Failure_trying_to_read_scan_IDs);
                }
            }
            return scanIdBytes;
        }

        public bool TryLoadChromatogramInfo(PeptideDocNode nodePep, TransitionGroupDocNode nodeGroup,
                                            float tolerance, out ChromatogramGroupInfo[] infoSet)
        {
            ChromGroupHeaderInfo5[] headers;
            if (TryLoadChromInfo(nodePep, nodeGroup, tolerance, out headers))
            {
                var infoSetNew = new ChromatogramGroupInfo[headers.Length];
                for (int i = 0; i < headers.Length; i++)
                {
                    infoSetNew[i] = LoadChromatogramInfo(headers[i]);
                }
                infoSet = infoSetNew;
                return true;
            }

            infoSet = new ChromatogramGroupInfo[0];
            return false;            
        }

        public bool HasAllIonsChromatograms
        {
            get
            {
                ChromGroupHeaderInfo5[] headers;
                return TryLoadChromInfo(null, null, 0, out headers);
            }
        }

        public bool TryLoadAllIonsChromatogramInfo(ChromExtractor extractor, out ChromatogramGroupInfo[] infoSet)
        {
            ChromGroupHeaderInfo5[] headers;
            if (TryLoadChromInfo(null, null, 0, out headers))
            {
                var infoSetNew = new List<ChromatogramGroupInfo>();
                foreach (ChromGroupHeaderInfo5 chromGroupHeader in headers)
                {
                    if (chromGroupHeader.Extractor == extractor)
                        infoSetNew.Add(LoadChromatogramInfo(chromGroupHeader));
                }
                infoSet = infoSetNew.ToArray();
                return true;
            }
            infoSet = new ChromatogramGroupInfo[0];
            return false;            
        }

        public ChromatogramGroupInfo LoadChromatogramInfo(int index)
        {
            return LoadChromatogramInfo(_chromatogramEntries[index]);
        }

        public ChromatogramGroupInfo LoadChromatogramInfo(ChromGroupHeaderInfo5 chromGroupHeaderInfo)
        {
            return new ChromatogramGroupInfo(chromGroupHeaderInfo,
                                             _scoreTypeIndices,
                                             _cachedFiles,
                                             _chromTransitions,
                                             _chromatogramPeaks,
                                             _scores);
        }

        public int Count
        {
            get { return _chromatogramEntries.Length; }
        }

        public IEnumerable<ChromGroupHeaderInfo5> ChromGroupHeaderInfos
        {
            get { return Array.AsReadOnly(_chromatogramEntries); }
        }

        private ChromatogramCache ChangeCachePath(string prop)
        {
            return ChangeProp(ImClone(this), im => im.CachePath = prop);
        }        

        public void Dispose()
        {
            ReadStream.CloseStream();
        }

        private bool TryLoadChromInfo(PeptideDocNode nodePep, TransitionGroupDocNode nodeGroup,
                                      float tolerance, out ChromGroupHeaderInfo5[] headerInfos)
        {
            float precursorMz = nodeGroup != null ? (float)nodeGroup.PrecursorMz : 0;
            int i = FindEntry(precursorMz, tolerance);
            if (i == -1)
            {
                headerInfos = new ChromGroupHeaderInfo5[0];
                return false;
            }

            // Add entries to a list until they no longer match
            var listChromatograms = new List<ChromGroupHeaderInfo5>();
            for (; i < _chromatogramEntries.Length && MatchMz(precursorMz, _chromatogramEntries[i].Precursor, tolerance); i++)
            {
                if (nodePep != null && !SequenceEqual(i, nodePep.ModifiedSequence))
                    continue;

                listChromatograms.Add(_chromatogramEntries[i]);
            }

            headerInfos = listChromatograms.ToArray();
            return headerInfos.Length > 0;
        }

        private bool SequenceEqual(int entryIndex, string modifiedSequence)
        {
            // Older format cache files will not have stored sequence bytes
            if (Version < FORMAT_VERSION_CACHE_5 && _seqBytes == null)
                return true;
            int seqIndex = _chromatogramEntries[entryIndex].SeqIndex;
            if (seqIndex == -1)
                return true;
            int seqLen = _chromatogramEntries[entryIndex].SeqLen;
            if (seqLen != modifiedSequence.Length)
                return false;
            for (int i = 0; i < seqLen; i++)
            {
                if (_seqBytes[seqIndex + i] != (byte) modifiedSequence[i])
                    return false;
            }
            return true;
        }

        private int FindEntry(float precursorMz, float tolerance)
        {
            if (_chromatogramEntries == null)
                return -1;
            return FindEntry(precursorMz, tolerance, 0, _chromatogramEntries.Length - 1);
        }

        private int FindEntry(double precursorMz, float tolerance, int left, int right)
        {
            // Binary search for the right precursorMz
            if (left > right)
                return -1;
            int mid = (left + right) / 2;
            int compare = CompareMz(precursorMz, _chromatogramEntries[mid].Precursor, tolerance);
            if (compare < 0)
                return FindEntry(precursorMz, tolerance, left, mid - 1);
            if (compare > 0)
                return FindEntry(precursorMz, tolerance, mid + 1, right);
            
            // Scan backward until the first matching element is found.
            while (mid > 0 && MatchMz(precursorMz, _chromatogramEntries[mid - 1].Precursor, tolerance))
                mid--;

            return mid;
        }

        private static int CompareMz(double precursorMz1, double precursorMz2, float tolerance)
        {
            return ChromKey.CompareTolerant(precursorMz1, precursorMz2,
                tolerance);
        }

        private static bool MatchMz(double mz1, double mz2, float tolerance)
        {
            return CompareMz(mz1, mz2, tolerance) == 0;
        }

        // ReSharper disable UnusedMember.Local
        private enum Header
        {
            // Version 9 header addition
            location_scan_ids_lo,
            location_scan_ids_hi,

            // Version 5 header addition
            num_score_types,
            num_scores,
            location_scores_lo,
            location_scores_hi,
            num_seq_bytes,
            location_seq_bytes_lo,
            location_seq_bytes_hi,

            format_version,
            num_peaks,
            location_peaks_lo,
            location_peaks_hi,
            num_transitions,
            location_trans_lo,
            location_trans_hi,
            num_chromatograms,
            location_headers_lo,
            location_headers_hi,
            num_files,
            location_files_lo,
            location_files_hi,

            count
        }

        public static int HeaderSize
        {
            get
            {
                var headerFirst = FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_4
                                     ? FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_8
                                        ? Header.location_scan_ids_lo : Header.num_score_types
                                     : Header.format_version;
                return (Header.count - headerFirst)*sizeof (int);
            }
        }

        private enum FileHeader
        {
            modified_lo,
            modified_hi,
            len_path,
            // Version 3 file header addition
            runstart_lo,
            runstart_hi,
            // Version 4 file header addition
            len_instrument_info,
            // Version 5 file header addition
            flags,
            // Version 6 file header addition
            max_retention_time,
            max_intensity,
            // Version 9 file header addition
            size_scan_ids,
            location_scan_ids_lo,
            location_scan_ids_hi,

            count,
            count2 = runstart_lo,
            count3 = len_instrument_info,
            count4 = flags,
            count5 = max_retention_time,
            count6 = size_scan_ids
        }
        // ReSharper restore UnusedMember.Local

        public struct RawData
        {
            public static readonly RawData EMPTY = new RawData
                {
                    ChromCacheFiles = new ChromCachedFile[0],
                    ChromatogramEntries = new ChromGroupHeaderInfo5[0],
                    ChromTransitions = new ChromTransition[0],
                    ChromatogramPeaks = new BlockedArray<ChromPeak>(),
                    ScoreTypes = new Type[0],
                    Scores = new float[0],
                };

            public int FormatVersion { get; set; }
            public ChromCachedFile[] ChromCacheFiles { get; set; }
            public ChromGroupHeaderInfo5[] ChromatogramEntries { get; set; }
            public ChromTransition[] ChromTransitions { get; set; }
            public BlockedArray<ChromPeak> ChromatogramPeaks { get; set; }
            public Type[] ScoreTypes { get; set; }
            public float[] Scores { get; set; }
            public byte[] SeqBytes { get; set; }
            public long LocationScanIds { get; set; }
            public long CountBytesScanIds { get; set; }

            public void RecalcEntry(int entryIndex,
                int offsetFiles,
                int offsetTransitions,
                int offsetPeaks,
                int offsetScores,
                long offsetPoints,
                Dictionary<string, int> dictSequenceToByteIndex,
                List<byte> listSeqBytes)
            {
                ChromatogramEntries[entryIndex].Offset(offsetFiles,
                    offsetTransitions,
                    offsetPeaks,
                    offsetScores,
                    offsetPoints);
                ChromatogramEntries[entryIndex].CalcSeqIndex(GetSequence(entryIndex),
                    dictSequenceToByteIndex,
                    listSeqBytes);
            }
            
            private string GetSequence(int entryIndex)
            {
                int seqIndex = ChromatogramEntries[entryIndex].SeqIndex;
                if (seqIndex == -1)
                    return null;
                int seqLen = ChromatogramEntries[entryIndex].SeqLen;
                return Encoding.Default.GetString(SeqBytes, seqIndex, seqLen);
            }
        }

        public static ChromatogramCache Load(string cachePath, ProgressStatus status, ILoadMonitor loader)
        {
            status = status.ChangeMessage(string.Format(Resources.ChromatogramCache_Load_Loading__0__cache, Path.GetFileName(cachePath)));
            loader.UpdateProgress(status);

            IPooledStream readStream = null;
            try
            {
                readStream = loader.StreamManager.CreatePooledStream(cachePath, false);

                RawData raw;
                LoadStructs(readStream.Stream, out raw);

                var result = new ChromatogramCache(cachePath, raw, readStream);
                loader.UpdateProgress(status.Complete());
                return result;
            }
            finally
            {
                if (readStream != null)
                {
                    // Close the read stream to ensure we never leak it.
                    // This only costs on extra open, the first time the
                    // active document tries to read.
                    try { readStream.CloseStream(); }
                    catch (IOException) { }
                }
            }
        }

        public static void Join(string cachePath, IPooledStream streamDest,
            IList<string> listCachePaths, ProgressStatus status, ILoadMonitor loader,
            Action<ChromatogramCache, Exception> complete)
        {
            try
            {
                var joiner = new ChromCacheJoiner(cachePath, streamDest, listCachePaths, loader, status, complete);
                joiner.JoinParts();
            }
            catch (Exception x)
            {
                complete(null, x);
            }
        }

        public static void Build(SrmDocument document, ChromatogramCache cacheRecalc,
            string cachePath, IList<MsDataFileUri> listResultPaths, ProgressStatus status, ILoadMonitor loader,
            Action<ChromatogramCache, Exception> complete)
        {
            try
            {
                var builder = new ChromCacheBuilder(document, cacheRecalc, cachePath, listResultPaths, loader, status, complete);
                builder.BuildCache();
            }
            catch (Exception x)
            {
                complete(null, x);
            }
        }

        public static long LoadStructs(Stream stream, out RawData raw)
        {
            // Read library header from the end of the cache
            const int countHeader = (int)Header.count * 4;
            stream.Seek(-countHeader, SeekOrigin.End);

            byte[] cacheHeader = new byte[countHeader];
            ReadComplete(stream, cacheHeader, countHeader);

            int formatVersion = GetInt32(cacheHeader, (int) Header.format_version);
            if (formatVersion > FORMAT_VERSION_CACHE)
            {
                throw new IOException(TextUtil.LineSeparate(string.Format(Resources.ChromatogramCache_LoadStructs_The_SKYD_file_format__0__is_not_supported_by_Skyline__1__,
                                                                          formatVersion, Install.Version),
                                                            Resources.ChromatogramCache_LoadStructs_Please_check_for_a_newer_release_));
            }
            if (formatVersion < FORMAT_VERSION_CACHE_2)
            {
                return EmptyCache(out raw);
            }

            raw = new RawData { FormatVersion =  formatVersion };
            int numPeaks = GetInt32(cacheHeader, (int)Header.num_peaks);
            long locationPeaks = BitConverter.ToInt64(cacheHeader, ((int)Header.location_peaks_lo) * 4);
            int numChrom = GetInt32(cacheHeader, (int)Header.num_chromatograms);
            long locationTrans = BitConverter.ToInt64(cacheHeader, ((int)Header.location_trans_lo) * 4);
            int numFiles = GetInt32(cacheHeader, (int)Header.num_files);
            long locationHeaders = BitConverter.ToInt64(cacheHeader, ((int)Header.location_headers_lo) * 4);
            int numTrans = GetInt32(cacheHeader, (int)Header.num_transitions);
            long locationFiles = BitConverter.ToInt64(cacheHeader, ((int)Header.location_files_lo) * 4);
            int numScoreTypes = 0, numScores = 0;
            long locationScoreTypes = locationPeaks;
            int numSeqBytes = 0;
            long locationSeqBytes = locationPeaks;
            if (formatVersion > FORMAT_VERSION_CACHE_4)
            {
                numScoreTypes = GetInt32(cacheHeader, (int)Header.num_score_types);
                numScores = GetInt32(cacheHeader, (int)Header.num_scores);
                locationScoreTypes = BitConverter.ToInt64(cacheHeader, ((int)Header.location_scores_lo) * 4);
                numSeqBytes = GetInt32(cacheHeader, (int)Header.num_seq_bytes);
                locationSeqBytes = BitConverter.ToInt64(cacheHeader, ((int)Header.location_seq_bytes_lo) * 4);
            }
            raw.LocationScanIds = locationPeaks;
            if (formatVersion > FORMAT_VERSION_CACHE_8)
            {
                raw.LocationScanIds = BitConverter.ToInt64(cacheHeader, ((int)Header.location_scan_ids_lo) * 4);
            }
            raw.CountBytesScanIds = locationPeaks - raw.LocationScanIds;

            // Unexpected empty cache.  Return values that will force it to be completely rebuild.
            if (numFiles == 0)
            {
                return EmptyCache(out raw);
            }

            // Read list of files cached
            stream.Seek(locationFiles, SeekOrigin.Begin);
            raw.ChromCacheFiles = new ChromCachedFile[numFiles];
            var countFileHeader = GetFileHeaderCount(formatVersion);

            byte[] fileHeader = new byte[countFileHeader];
            byte[] filePathBuffer = new byte[1024];
            for (int i = 0; i < numFiles; i++)
            {
                ReadComplete(stream, fileHeader, countFileHeader);
                long modifiedBinary = BitConverter.ToInt64(fileHeader, ((int)FileHeader.modified_lo) * 4);
                int lenPath = GetInt32(fileHeader, (int)FileHeader.len_path);
                ReadComplete(stream, filePathBuffer, lenPath);
                string filePathString = formatVersion > FORMAT_VERSION_CACHE_6
                                      ? Encoding.UTF8.GetString(filePathBuffer, 0, lenPath)
                                      : Encoding.Default.GetString(filePathBuffer, 0, lenPath);
                var filePath = MsDataFileUri.Parse(filePathString);
                long runstartBinary = (IsVersionCurrent(formatVersion)
                                           ? BitConverter.ToInt64(fileHeader, ((int)FileHeader.runstart_lo) * 4)
                                           : 0);

                ChromCachedFile.FlagValues fileFlags = 0;
                if (formatVersion > FORMAT_VERSION_CACHE_4)
                    fileFlags = (ChromCachedFile.FlagValues) GetInt32(fileHeader, (int) FileHeader.flags);
                float maxRT = 0, maxIntensity = 0;
                if (formatVersion > FORMAT_VERSION_CACHE_5)
                {
                    maxRT = GetFloat(fileHeader, (int)FileHeader.max_retention_time);
                    maxIntensity = GetFloat(fileHeader, (int)FileHeader.max_intensity);
                }
                int sizeScanIds = 0;
                long locationScanIds = 0;
                if (formatVersion > FORMAT_VERSION_CACHE_8)
                {
                    sizeScanIds = GetInt32(fileHeader, (int) FileHeader.size_scan_ids);
                    locationScanIds = BitConverter.ToInt64(fileHeader, ((int) FileHeader.location_scan_ids_lo)*4);
                }
                string instrumentInfoStr = null;
                if (formatVersion > FORMAT_VERSION_CACHE_3)
                {
                    int lenInstrumentInfo = GetInt32(fileHeader, (int) FileHeader.len_instrument_info);
                    byte[] instrumentInfoBuffer = new byte[lenInstrumentInfo];
                    ReadComplete(stream, instrumentInfoBuffer, lenInstrumentInfo);
                    instrumentInfoStr = Encoding.UTF8.GetString(instrumentInfoBuffer, 0, lenInstrumentInfo);
                }

                DateTime modifiedTime = DateTime.FromBinary(modifiedBinary);
                DateTime? runstartTime = runstartBinary != 0 ? DateTime.FromBinary(runstartBinary) : (DateTime?) null;
                var instrumentInfoList = InstrumentInfoUtil.GetInstrumentInfo(instrumentInfoStr);
                raw.ChromCacheFiles[i] = new ChromCachedFile(filePath,
                                                             fileFlags,
                                                             modifiedTime,
                                                             runstartTime,
                                                             maxRT,
                                                             maxIntensity,
                                                             sizeScanIds,
                                                             locationScanIds,
                                                             instrumentInfoList);
            }

            // Read list of chromatogram group headers
            stream.Seek(locationHeaders, SeekOrigin.Begin);
            raw.ChromatogramEntries = ChromGroupHeaderInfo5.ReadArray(stream, numChrom, formatVersion);

            if (formatVersion > FORMAT_VERSION_CACHE_4)
            {
                // Read sequence bytes
                raw.SeqBytes = new byte[numSeqBytes];
                stream.Seek(locationSeqBytes, SeekOrigin.Begin);
                ReadComplete(stream, raw.SeqBytes, raw.SeqBytes.Length);
            }
            else
            {
                raw.SeqBytes = null;
            }
            if (formatVersion > FORMAT_VERSION_CACHE_4 && numScoreTypes > 0)
            {
                // Read scores
                raw.ScoreTypes = new Type[numScoreTypes];
                stream.Seek(locationScoreTypes, SeekOrigin.Begin);
                byte[] scoreTypeLengths = new byte[numScoreTypes * 4];
                byte[] typeNameBuffer = new byte[1024];
                ReadComplete(stream, scoreTypeLengths, scoreTypeLengths.Length);
                for (int i = 0; i < numScoreTypes; i++)
                {
                    int lenTypeName = GetInt32(scoreTypeLengths, i);
                    ReadComplete(stream, typeNameBuffer, lenTypeName);
                    raw.ScoreTypes[i] = Type.GetType(Encoding.Default.GetString(typeNameBuffer, 0, lenTypeName));
                }
                raw.Scores = PrimitiveArrays.Read<float>(stream, numScores);
            }
            else
            {
                raw.ScoreTypes = new Type[0];
                raw.Scores = new float[0];
            }

            // Read list of transitions
            stream.Seek(locationTrans, SeekOrigin.Begin);
            raw.ChromTransitions = ChromTransition.ReadArray(stream, numTrans, formatVersion);

            // Read list of peaks
            stream.Seek(locationPeaks, SeekOrigin.Begin);
            raw.ChromatogramPeaks = new BlockedArray<ChromPeak>(
                count => ChromPeak.ReadArray(stream, count), 
                numPeaks, 
                ChromPeak.SizeOf,
                ChromPeak.DEFAULT_BLOCK_SIZE);

            return raw.LocationScanIds;  // Bytes of chromatogram data
        }

        private static int GetFileHeaderCount(int formatVersion)
        {
            switch (formatVersion)
            {
                case FORMAT_VERSION_CACHE_2:
                    return (int) (FileHeader.count2)*4;
                case FORMAT_VERSION_CACHE_3:
                    return (int) (FileHeader.count3)*4;
                case FORMAT_VERSION_CACHE_4:
                    return (int) (FileHeader.count4)*4;
                case FORMAT_VERSION_CACHE_5:
                    return (int) (FileHeader.count5)*4;
                case FORMAT_VERSION_CACHE_6:
                case FORMAT_VERSION_CACHE_7:
                case FORMAT_VERSION_CACHE_8:
                    return (int) (FileHeader.count6)*4;
                default:
                    return (int) (FileHeader.count)*4;
            }
        }

        private static long EmptyCache(out RawData raw)
        {
            raw = RawData.EMPTY;
            return 0;
        }

        private static int GetInt32(byte[] bytes, int index)
        {
            int ibyte = index * 4;
            return BitConverter.ToInt32(bytes, ibyte);
        }
        
        private static float GetFloat(byte[] bytes, int index)
        {
            int ibyte = index * 4;
            return BitConverter.ToSingle(bytes, ibyte);
        }

        private static void ReadComplete(Stream stream, byte[] buffer, int size)
        {
            if (stream.Read(buffer, 0, size) != size)
                throw new InvalidDataException(Resources.ChromatogramCache_ReadComplete_Data_truncation_in_cache_header_File_may_be_corrupted);
        }

        /// <summary>
        /// Write mostly new information taken from an existing cache, without
        /// breaking encapsulation.
        /// </summary>
        public static void WriteStructs(Stream outStream,
                                        Stream outStreamScans,
                                        Stream outStreamPeaks,
                                        ICollection<ChromCachedFile> chromCachedFiles,
                                        List<ChromGroupHeaderInfo5> chromatogramEntries,
                                        ICollection<ChromTransition> chromTransitions,
                                        ICollection<Type> scoreTypes,
                                        float[] scores,
                                        int peakCount,
                                        ChromatogramCache originalCache)
        {
            WriteStructs(outStream,
                         outStreamScans,
                         outStreamPeaks,
                         chromCachedFiles,
                         chromatogramEntries,
                         chromTransitions,
                         originalCache._seqBytes,   // Cached sequence bytes remain unchanged
                         scoreTypes,
                         scores,
                         peakCount);
        }

        public static void WriteStructs(Stream outStream,
                                        Stream outStreamScans,
                                        Stream outStreamPeaks,
                                        ICollection<ChromCachedFile> chromCachedFiles,
                                        List<ChromGroupHeaderInfo5> chromatogramEntries,
                                        ICollection<ChromTransition> chromTransitions,
                                        ICollection<byte> seqBytes,
                                        ICollection<Type> scoreTypes,
                                        float[] scores,
                                        int peakCount)
        {
            long locationScans = outStream.Position;
            if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_8)
            {
                // Write any scan ids
                outStreamScans.Seek(0, SeekOrigin.Begin);
                outStreamScans.CopyTo(outStream);
            }

            // Write the picked peaks
            long locationPeaks = outStream.Position;
            outStreamPeaks.Seek(0, SeekOrigin.Begin);
            outStreamPeaks.CopyTo(outStream);

            // Write the transitions
            long locationTrans = outStream.Position;
            foreach (var tran in chromTransitions)
            {
                if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_4)
                {
                    outStream.Write(BitConverter.GetBytes(tran.Product), 0, sizeof(double));
                    outStream.Write(BitConverter.GetBytes(tran.ExtractionWidth), 0, sizeof(float));
                    if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_7)
                    {
                        outStream.Write(BitConverter.GetBytes(tran.IonMobilityValue), 0, sizeof(float));
                        outStream.Write(BitConverter.GetBytes(tran.IonMobilityExtractionWidth), 0, sizeof(float));
                    }
                    outStream.Write(BitConverter.GetBytes(tran.FlagBits), 0, sizeof(ushort));
                    outStream.Write(BitConverter.GetBytes(tran.Align1), 0, sizeof(ushort));
                }
                else
                {
                    outStream.Write(BitConverter.GetBytes((float)tran.Product), 0, sizeof(float));
                }
            }

            long locationScores = outStream.Position;
            long locationSeqBytes = outStream.Position;
            if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_4)
            {
                // Write the scores
                StringBuilder sbTypes = new StringBuilder();
                foreach (string scoreTypeName in scoreTypes.Select(scoreType => scoreType.ToString()))
                {
                    outStream.Write(BitConverter.GetBytes(scoreTypeName.Length), 0, sizeof(int));
                    sbTypes.Append(scoreTypeName);
                }
                int len = sbTypes.Length;
                if (len > 0)
                {
                    byte[] typesBuffer = new byte[len];
                    Encoding.Default.GetBytes(sbTypes.ToString(), 0, sbTypes.Length, typesBuffer, 0);
                    outStream.Write(typesBuffer, 0, len);
                    PrimitiveArrays.Write(outStream, scores);
                }

                // Write sequence bytes
                locationSeqBytes = outStream.Position;
                if (seqBytes != null && seqBytes.Count > 0)
                {
                    byte[] seqBytesBuffer = seqBytes.ToArray();
                    outStream.Write(seqBytesBuffer, 0, seqBytesBuffer.Length);
                }
            }

            // Write sorted list of chromatogram header info structs
            chromatogramEntries.Sort();

            long locationHeaders = outStream.Position;
            foreach (var info in chromatogramEntries)
            {
                long lastPeak = info.StartPeakIndex + info.NumPeaks*info.NumTransitions;
                if (lastPeak > peakCount)
                    throw new InvalidDataException(string.Format(Resources.ChromatogramCache_WriteStructs_Failure_writing_cache___Specified__0__peaks_exceed_total_peak_count__1_, lastPeak, peakCount));
                if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_4)
                {
                    outStream.Write(BitConverter.GetBytes(info.SeqIndex), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(info.StartTransitionIndex), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(info.StartPeakIndex), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(info.StartScoreIndex), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(info.NumPoints), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(info.CompressedSize), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(info.FlagBits), 0, sizeof(ushort));
                    outStream.Write(BitConverter.GetBytes(info.FileIndex), 0, sizeof(ushort));
                    outStream.Write(BitConverter.GetBytes(info.SeqLen), 0, sizeof(ushort));
                    outStream.Write(BitConverter.GetBytes(info.NumTransitions), 0, sizeof(ushort));
                    outStream.Write(new[] {info.NumPeaks, (byte)info.MaxPeakIndex}, 0, 2);
                    outStream.Write(BitConverter.GetBytes(info.Align1), 0, sizeof(ushort));
                    if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_5)
                    {
                        outStream.Write(BitConverter.GetBytes(info.StatusId), 0, sizeof(ushort));
                        outStream.Write(BitConverter.GetBytes(info.StatusRank), 0, sizeof(ushort));                        
                    }
                    else
                    {
                        outStream.Write(BitConverter.GetBytes(0), 0, sizeof(int));
                    }
                    outStream.Write(BitConverter.GetBytes(info.Precursor), 0, sizeof(double));
                    outStream.Write(BitConverter.GetBytes(info.LocationPoints), 0, sizeof(long));
                }
                else
                {
                    outStream.Write(BitConverter.GetBytes((float)info.Precursor), 0, sizeof(float));
                    outStream.Write(BitConverter.GetBytes((int)info.FileIndex), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes((int)info.NumTransitions), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(info.StartTransitionIndex), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes((int)info.NumPeaks), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(info.StartPeakIndex), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes((int)info.MaxPeakIndex), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(info.NumPoints), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(info.CompressedSize), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(0), 0, sizeof(int));  // Alignment for 64-bit LocationPoints value
                    outStream.Write(BitConverter.GetBytes(info.LocationPoints), 0, sizeof(long));
                }
            }

            // Write the list of cached files and their modification time stamps
            long locationFiles = outStream.Position;
            byte[] pathBuffer = new byte[0x1000];
            foreach (var cachedFile in chromCachedFiles)
            {
                long time = cachedFile.FileWriteTime.ToBinary();
                outStream.Write(BitConverter.GetBytes(time), 0, sizeof(long));
                string filePathString = cachedFile.FilePath.ToString();            
                int len = Encoding.UTF8.GetByteCount(filePathString);
                Encoding.UTF8.GetBytes(filePathString, 0, filePathString.Length, pathBuffer, 0);
                outStream.Write(BitConverter.GetBytes(len), 0, sizeof(int));
                // Version 3 write modified time
                var runStartTime = cachedFile.RunStartTime;
                time = (runStartTime.HasValue ? runStartTime.Value.ToBinary() : 0);
                outStream.Write(BitConverter.GetBytes(time), 0, sizeof(long));

                // Version 4 write instrument information
                string instrumentInfo = InstrumentInfoUtil.GetInstrumentInfoString(cachedFile.InstrumentInfoList);
                int instrumentInfoLen = Encoding.UTF8.GetByteCount(instrumentInfo);
                byte[] instrumentInfoBuffer = new byte[instrumentInfoLen];
                Encoding.UTF8.GetBytes(instrumentInfo, 0, instrumentInfo.Length, instrumentInfoBuffer, 0);
                outStream.Write(BitConverter.GetBytes(instrumentInfoLen), 0, sizeof(int));

                // Version 5 write flags
                if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_4)
                    outStream.Write(BitConverter.GetBytes((int) cachedFile.Flags), 0, sizeof(int));

                // Version 6 write time and intensity dimensions
                if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_5)
                {
                    outStream.Write(BitConverter.GetBytes(cachedFile.MaxRetentionTime), 0, sizeof(float));
                    outStream.Write(BitConverter.GetBytes(cachedFile.MaxIntensity), 0, sizeof(float));
                }

                // Version 9 write scan id info
                if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_8)
                {
                    outStream.Write(BitConverter.GetBytes(cachedFile.SizeScanIds), 0, sizeof(int));
                    outStream.Write(BitConverter.GetBytes(cachedFile.LocationScanIds), 0, sizeof(long));
                }

                // Write variable length buffers
                outStream.Write(pathBuffer, 0, len);
                outStream.Write(instrumentInfoBuffer, 0, instrumentInfoLen);
            }

            // Write the initial file header
            if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_8)
            {
                // scan ids
                outStream.Write(BitConverter.GetBytes(locationScans), 0, sizeof(long));
            }
            if (FORMAT_VERSION_CACHE > FORMAT_VERSION_CACHE_4)
            {
                // scores
                outStream.Write(BitConverter.GetBytes(scoreTypes.Count), 0, sizeof(int));
                outStream.Write(BitConverter.GetBytes(scores.Length), 0, sizeof(int));
                outStream.Write(BitConverter.GetBytes(locationScores), 0, sizeof(long));
                // sequence bytes
                int countSeqBytes = (seqBytes != null ? seqBytes.Count : 0);
                outStream.Write(BitConverter.GetBytes(countSeqBytes), 0, sizeof(int));
                outStream.Write(BitConverter.GetBytes(locationSeqBytes), 0, sizeof(long));
            }
            // The format version must remain in the same relative position as in the
            // original file.  Obviously, it should have been written as the last element,
            // and not the first above the other values.
            outStream.Write(BitConverter.GetBytes(FORMAT_VERSION_CACHE), 0, sizeof(int));
            outStream.Write(BitConverter.GetBytes(peakCount), 0, sizeof(int));
            outStream.Write(BitConverter.GetBytes(locationPeaks), 0, sizeof(long));
            outStream.Write(BitConverter.GetBytes(chromTransitions.Count), 0, sizeof(int));
            outStream.Write(BitConverter.GetBytes(locationTrans), 0, sizeof(long));
            outStream.Write(BitConverter.GetBytes(chromatogramEntries.Count), 0, sizeof(int));
            outStream.Write(BitConverter.GetBytes(locationHeaders), 0, sizeof(long));
            outStream.Write(BitConverter.GetBytes(chromCachedFiles.Count), 0, sizeof(int));
            outStream.Write(BitConverter.GetBytes(locationFiles), 0, sizeof(long));
        }

        public static void BytesToTimeIntensities(byte[] bytes, int numPoints, int numTrans, bool withErrors,
            bool withMs1ScanIds, bool withFragmentScanIds, bool withSimScanIds,
            out float[] times, out float[][] intensities, out short[][] massErrors, out int[][] scanIds)
        {
            times = new float[numPoints];
            intensities = new float[numTrans][];
            massErrors = withErrors ? new short[numTrans][] : null;
            scanIds = null; 

            int sizeArray = sizeof(float)*numPoints;
            Buffer.BlockCopy(bytes, 0, times, 0, sizeArray);
            int offset = sizeArray;
            for (int i = 0; i < numTrans; i++, offset += sizeArray)
            {
                intensities[i] = new float[numPoints];
                Buffer.BlockCopy(bytes, offset, intensities[i], 0, sizeArray);
            }
            if (withErrors)
            {
                int sizeArrayErrors = sizeof(short)*numPoints;
                for (int i = 0; i < numTrans; i++, offset += sizeArrayErrors)
                {
                    massErrors[i] = new short[numPoints];
                    Buffer.BlockCopy(bytes, offset, massErrors[i], 0, sizeArrayErrors);
                }
            }
            if (withMs1ScanIds || withFragmentScanIds || withSimScanIds)
            {
                scanIds = new int[Helpers.CountEnumValues<ChromSource>() - 1][];
                int sizeArrayScanIds = sizeof(int) * numPoints;
                if (withMs1ScanIds)
                    scanIds[(int)ChromSource.ms1] = new int[numPoints];
                if (withFragmentScanIds)
                    scanIds[(int)ChromSource.fragment] = new int[numPoints];
                if (withSimScanIds)
                    scanIds[(int)ChromSource.sim] = new int[numPoints];
                for (int source = 0; source < scanIds.Length; source++)
                {
                    if (scanIds[source] != null)
                    {
                        Buffer.BlockCopy(bytes, offset, scanIds[source], 0, sizeArrayScanIds);
                        offset += sizeArrayScanIds;
        }
                }
            }
        }

        public static byte[] TimeIntensitiesToBytes(float[] times, float[][] intensities, short[][] massErrors, int[][] scanIds)
        {
            int numPoints = times.Length;
            int sizeArray = numPoints*sizeof(float);
            int numTrans = intensities.Length;
            bool hasErrors = massErrors != null;
            bool hasMs1ScanIds = scanIds != null && scanIds[(int) ChromSource.ms1] != null;
            bool hasFragmentScanIds = scanIds != null && scanIds[(int) ChromSource.fragment] != null;
            bool hasSimScanIds = scanIds != null && scanIds[(int) ChromSource.sim] != null;
            byte[] points = new byte[GetChromatogramsByteCount(numTrans, numPoints, hasErrors, hasMs1ScanIds, hasFragmentScanIds, hasSimScanIds)];

            // Write times
            Buffer.BlockCopy(times, 0, points, 0, sizeArray);
            int offset = sizeArray;

            // Write intensites
            for (int i = 0; i < numTrans; i++, offset += sizeArray)
            {
                Buffer.BlockCopy(intensities[i], 0, points, offset, sizeArray);
            }

            // Write mass errors, if provided
            if (hasErrors)
            {
                int sizeArrayErrors = numPoints*sizeof(short);
                for (int i = 0; i < numTrans; i++, offset += sizeArrayErrors)
                {
                    Buffer.BlockCopy(massErrors[i], 0, points, offset, sizeArrayErrors);
                }
            }

            // Write scan ids, if provided
            if (scanIds != null)
            {
                int sizeArrayScanIds = numPoints*sizeof(int);
                for (int source = 0; source < scanIds.Length; source++)
                {
                    if (scanIds[source] != null)
                    {
                        Buffer.BlockCopy(scanIds[source], 0, points, offset, sizeArrayScanIds);
                        offset += sizeArrayScanIds;
                    }
                }
            }

            return points;
        }

        public static int GetChromatogramsByteCount(int numTrans, int numPoints, bool hasErrors, 
            bool hasMs1ScanIds, bool hasFragmentScanIds, bool hasSimScanIds)
        {
            int sizeArray = sizeof(float)*numPoints;
            int sizeArrayErrors = sizeof(short)*numPoints;
            int sizeTotal = sizeArray*(numTrans + 1);
            if (hasErrors)
                sizeTotal += sizeArrayErrors*numTrans;
            if (hasMs1ScanIds)
                sizeTotal += sizeof (int)*numPoints;
            if (hasFragmentScanIds)
                sizeTotal += sizeof (int)*numPoints;
            if (hasSimScanIds)
                sizeTotal += sizeof (int)*numPoints;
            return sizeTotal;
        }

        public void GetStatusDimensions(MsDataFileUri msDataFilePath, out float? maxRetentionTime, out float? maxIntensity)
        {
            int fileIndex = CachedFiles.IndexOf(f => Equals(f.FilePath, msDataFilePath));
            if (fileIndex == -1)
            {
                maxRetentionTime = maxIntensity = null;
            }
            else
            {
                var cacheFile = CachedFiles[fileIndex];
                maxRetentionTime = cacheFile.MaxRetentionTime;
                maxIntensity = cacheFile.MaxIntensity;
            }
        }

        public IEnumerable<ChromKeyIndices> GetChromKeys(MsDataFileUri msDataFilePath)
        {
            int fileIndex = CachedFiles.IndexOf(f => Equals(f.FilePath, msDataFilePath));
            if (fileIndex == -1)
                yield break;

            for (int i = 0; i < _chromatogramEntries.Length; i++)
            {
                var groupInfo = _chromatogramEntries[i];
                if (groupInfo.FileIndex != fileIndex)
                    continue;

                for (int j = 0; j < groupInfo.NumTransitions; j++)
                {
                    int tranIndex = groupInfo.StartTransitionIndex + j;
                    var tranInfo = _chromTransitions[tranIndex];
                    double product = tranInfo.Product;
                    float extractionWidth = tranInfo.ExtractionWidth;
                    ChromSource source = tranInfo.Source;
                    ChromKey key = new ChromKey(_seqBytes, groupInfo.SeqIndex, groupInfo.SeqLen,
                        groupInfo.Precursor, product, extractionWidth, tranInfo.IonMobilityValue, tranInfo.IonMobilityExtractionWidth,  source, groupInfo.Extractor, true, true);

                    int id = groupInfo.HasStatusId ? groupInfo.StatusId : i;
                    int rank = groupInfo.HasStatusRank ? groupInfo.StatusRank : -1;
                    yield return new ChromKeyIndices(key, groupInfo.LocationPoints, i, id, rank, j);
                }
            }
        }

        public ChromatogramCache Optimize(string documentPath, IEnumerable<MsDataFileUri> msDataFilePaths, IStreamManager streamManager)
        {
            string cachePathOpt = FinalPathForName(documentPath, null);

            var cachedFilePaths = new HashSet<MsDataFileUri>(CachedFilePaths);
            cachedFilePaths.IntersectWith(msDataFilePaths);
            // If the cache contains only the files in the document, then no
            // further optimization is necessary.
            if (cachedFilePaths.Count == CachedFiles.Count)
            {
                if (Equals(cachePathOpt, CachePath))
                    return this;
                // Copy the cache, if moving to a new location
                using (FileSaver fs = new FileSaver(cachePathOpt))
                {
                    File.Copy(CachePath, fs.SafeName, true);
                    fs.Commit(ReadStream);                    
                }
                return ChangeCachePath(cachePathOpt);
            }

            Assume.IsTrue(cachedFilePaths.Count > 0);

            // Create a copy of the headers
            var listEntries = new List<ChromGroupHeaderInfo5>(_chromatogramEntries);
            // Sort by file, points location
            listEntries.Sort((e1, e2) =>
                                 {
                                     int result = Comparer.Default.Compare(e1.FileIndex, e2.FileIndex);
                                     if (result != 0)
                                         return result;
                                     return Comparer.Default.Compare(e1.LocationPoints, e2.LocationPoints);
                                 });

            var listKeepEntries = new List<ChromGroupHeaderInfo5>();
            var listKeepCachedFiles = new List<ChromCachedFile>();
            var listKeepTransitions = new List<ChromTransition>();
            var listKeepSeqBytes = new List<byte>();
            var dictKeepSeqIndices = new Dictionary<int, int>();
            var listKeepScores = new List<float>();
            var scoreTypes = ScoreTypes.ToArray();

            using (FileSaver fsPeaks = new FileSaver(cachePathOpt + PEAKS_EXT, true))
            using (FileSaver fsScans = new FileSaver(cachePathOpt + SCANS_EXT, true))
            using (FileSaver fs = new FileSaver(cachePathOpt))
            {
                var inStream = ReadStream.Stream;
                fs.Stream = streamManager.CreateStream(fs.SafeName, FileMode.Create, true);
                var peakCount = 0;

                byte[] buffer = new byte[0x40000];  // 256K

                int i = 0;
                do
                {
                    var firstEntry = listEntries[i];
                    var lastEntry = firstEntry;
                    int fileIndex = firstEntry.FileIndex;
                    bool keepFile = cachedFilePaths.Contains(_cachedFiles[fileIndex].FilePath);
                    long offsetPoints = fs.Stream.Position - firstEntry.LocationPoints;

                    int iNext = i;
                    // Enumerate until end of current file encountered
                    while (iNext < listEntries.Count && fileIndex == listEntries[iNext].FileIndex)
                    {
                        lastEntry = listEntries[iNext++];
                        // If discarding this file, just skip its entries
                        if (!keepFile)
                            continue;
                        // Otherwise add entries to the keep lists
                        int seqIndex = -1;
                        if (lastEntry.SeqIndex != -1 && !dictKeepSeqIndices.TryGetValue(lastEntry.SeqIndex, out seqIndex))
                        {
                            seqIndex = listKeepSeqBytes.Count;
                            dictKeepSeqIndices.Add(lastEntry.SeqIndex, seqIndex);
                        }
                        listKeepEntries.Add(new ChromGroupHeaderInfo5(lastEntry.Precursor,
                                                                      seqIndex,
                                                                      lastEntry.SeqLen,
                                                                      listKeepCachedFiles.Count,
                                                                      lastEntry.NumTransitions,
                                                                      listKeepTransitions.Count,
                                                                      lastEntry.NumPeaks,
                                                                      peakCount,
                                                                      listKeepScores.Count,
                                                                      lastEntry.MaxPeakIndex,
                                                                      lastEntry.NumPoints,
                                                                      lastEntry.CompressedSize,
                                                                      lastEntry.LocationPoints + offsetPoints,
                                                                      lastEntry.Flags,
                                                                      lastEntry.StatusId,
                                                                      lastEntry.StatusRank));
                        int start = lastEntry.StartTransitionIndex;
                        int end = start + lastEntry.NumTransitions;
                        for (int j = start; j < end; j++)
                            listKeepTransitions.Add(_chromTransitions[j]);
                        start = lastEntry.StartPeakIndex;
                        end = start + lastEntry.NumPeaks*lastEntry.NumTransitions;
                        peakCount += end - start;
                        _chromatogramPeaks.WriteArray(
                            (peaks, startIndex, count) =>
                            ChromPeak.WriteArray(fsPeaks.FileStream.SafeFileHandle, peaks, startIndex, count),
                            start,
                            end - start);

                        start = lastEntry.SeqIndex;
                        end = start + lastEntry.SeqLen;
                        for (int j = start; j < end; j++)
                            listKeepSeqBytes.Add(_seqBytes[j]);

                        start = lastEntry.StartScoreIndex;
                        end = start + lastEntry.NumPeaks*scoreTypes.Length;
                        for (int j = start; j < end; j++)
                            listKeepScores.Add(_scores[j]);
                    }

                    if (keepFile)
                    {
                        if (_cachedFiles[fileIndex].SizeScanIds == 0)
                            listKeepCachedFiles.Add(_cachedFiles[fileIndex]);
                        else
                        {
                            // Write all scan ids for the last file to the scan ids output stream
                            inStream.Seek(_locationScanIds + _cachedFiles[fileIndex].LocationScanIds, SeekOrigin.Begin);
                            int lenReadIds = _cachedFiles[fileIndex].SizeScanIds;
                            long locationScanIds = fsScans.Stream.Position;
                            inStream.TransferBytes(fsScans.Stream, lenReadIds, buffer);
                            listKeepCachedFiles.Add(_cachedFiles[fileIndex].RelocateScanIds(locationScanIds));
                        }

                        // Write all points for the last file to the output stream
                        inStream.Seek(firstEntry.LocationPoints, SeekOrigin.Begin);
                        long lenRead = lastEntry.LocationPoints + lastEntry.CompressedSize - firstEntry.LocationPoints;
                        inStream.TransferBytes(fs.Stream, lenRead, buffer);
                    }

                    // Advance to next file
                    i = iNext;
                }
                while (i < listEntries.Count);

                long locationAllScanIds = fs.Stream.Position;
                long countBytesAllScanIds = fsScans.Stream.Position;

                WriteStructs(fs.Stream,
                    fsScans.Stream,
                    fsPeaks.Stream,
                    listKeepCachedFiles,
                    listKeepEntries,
                    listKeepTransitions,
                    listKeepSeqBytes,
                    scoreTypes,
                    listKeepScores.ToArray(),
                    peakCount);

                CommitCache(fs);

                fsPeaks.Stream.Seek(0, SeekOrigin.Begin);
                var rawData = new RawData
                    {
                        FormatVersion = FORMAT_VERSION_CACHE,
                        ChromCacheFiles = listKeepCachedFiles.ToArray(),
                        ChromatogramEntries = listKeepEntries.ToArray(),
                        ChromTransitions = listKeepTransitions.ToArray(),
                        ChromatogramPeaks = new BlockedArray<ChromPeak>(
                            count => ChromPeak.ReadArray(fsPeaks.FileStream.SafeFileHandle, count), peakCount,
                            ChromPeak.SizeOf, ChromPeak.DEFAULT_BLOCK_SIZE),
                        SeqBytes = listKeepSeqBytes.ToArray(),
                        ScoreTypes = scoreTypes,
                        Scores = listKeepScores.ToArray(),
                        LocationScanIds = locationAllScanIds,
                        CountBytesScanIds = countBytesAllScanIds,
                    };
                return new ChromatogramCache(cachePathOpt,
                                             rawData,
                                             // Create a new read stream, for the newly created file
                                             streamManager.CreatePooledStream(cachePathOpt, false));
            }
        }

        public void WriteScanIds(FileStream outputStreamScans)
        {
            if (_countBytesScanIds == 0)
                return;

            var stream = ReadStream.Stream;
            stream.Seek(_locationScanIds, SeekOrigin.Begin);
            ReadStream.Stream.TransferBytes(outputStreamScans, _countBytesScanIds);
        }

        public void CommitCache(FileSaver fs)
        {
            // Close the read stream, in case the destination is the source, and
            // overwrite is necessary.
            ReadStream.CloseStream();
            fs.Commit(ReadStream);
        }

        public class PathEqualityComparer : IEqualityComparer<ChromatogramCache>
        {
            public bool Equals(ChromatogramCache x, ChromatogramCache y)
            {
                return Equals(x.CachePath, y.CachePath);
            }

            public int GetHashCode(ChromatogramCache obj)
            {
                return obj.CachePath.GetHashCode();
            }
        }

        public static PathEqualityComparer PathComparer { get; private set; }

        static ChromatogramCache()
        {
            PathComparer = new PathEqualityComparer();
        }
    }

    public struct ChromKeyIndices
    {
        public ChromKeyIndices(ChromKey key, long locationPoints, int groupIndex, int statusId, int statusRank, int tranIndex)
            : this()
        {
            Key = key;
            LocationPoints = locationPoints;
            GroupIndex = groupIndex;
            StatusId = statusId;
            StatusRank = statusRank;
            TranIndex = tranIndex;
        }

        public ChromKey Key { get; private set; }
        public long LocationPoints { get; private set; }
        public int GroupIndex { get; private set; }
        public int StatusId { get; private set; }
        public int StatusRank { get; private set; }
        public int TranIndex { get; private set; }
    }
}