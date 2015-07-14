﻿/*
 * Original author: Brendan MacLean <brendanx .at. u.washington.edu>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2013 University of Washington - Seattle, WA
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

using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using pwiz.Common.DataBinding;
using pwiz.Common.DataBinding.Controls.Editor;
using pwiz.Skyline.Controls;
using pwiz.Skyline.Controls.Databinding;
using pwiz.Skyline.Controls.Graphs;
using pwiz.Skyline.FileUI;
using pwiz.Skyline.Model;
using pwiz.Skyline.Model.Databinding;
using pwiz.Skyline.Model.DocSettings;
using pwiz.Skyline.Model.Hibernate;
using pwiz.Skyline.Model.Hibernate.Query;
using pwiz.Skyline.Properties;
using pwiz.Skyline.SettingsUI;
using pwiz.Skyline.Util;
using pwiz.SkylineTestUtil;

namespace pwiz.SkylineTestFunctional
{
    /// <summary>
    /// Test of peptide standard types (iRT, QC and Normalization)
    /// </summary>
    [TestClass]
    public class StandardTypeTest : AbstractFunctionalTestEx
    {
        // Select peptide index 16
        const int SELECTED_PEPTIDE_INDEX = 16;

        private bool AsSmallMolecules { get; set; }


        [TestMethod]
        public void TestStandardType()
        {
            RunTestStandardType(false);
        }

        [TestMethod]
        public void TestStandardTypeAsSmallMolecules()
        {
            RunTestStandardType(true);
        }

        private void RunTestStandardType(bool asSmallMolecules)
        {
            AsSmallMolecules = asSmallMolecules;
            if (AsSmallMolecules)
                TestDirectoryName = "AsSmallMolecules";
            TestFilesZip = @"TestFunctional\StandardTypeTest.zip";
            RunFunctionalTest();
        }


        //[TestMethod]
        public void TestStandardTypeWithOldReports()
        {
            RunWithOldReports(TestStandardType);
        }

        protected override void DoTest()
        {
            TestSmallMolecules = false; // Don't need that magic extra node, we have an explict test

            // Open the SRMCourse.sky file
            string documentPath1 = TestFilesDir.GetTestPath("SRMCourse.sky");
            RunUI(() => SkylineWindow.OpenFile(documentPath1));
            WaitForDocumentLoaded();
            if (AsSmallMolecules)
            {
                ConvertDocumentToSmallMolecules(ignoreDecoys:true);
            }
            else
            {
                var peps = SkylineWindow.Document.Molecules.ToArray();
                Assert.IsNull(peps[0].GlobalStandardType);
                Assert.IsTrue(peps.Skip(1).Take(10).All(nodePep =>
                    Equals(nodePep.GlobalStandardType, PeptideDocNode.STANDARD_TYPE_IRT)));
                Assert.IsTrue(peps.Skip(11).Take(3).All(nodePep => nodePep.GlobalStandardType == null));
            }
           
            // Test Set Standard Type menus
            // Non-peptide selected (CONSIDER: Should this allow setting standard type on all peptides?)
            SelectNode(SrmDocument.Level.MoleculeGroups, 0);
            RunUI(() => SkylineWindow.ShowTreeNodeContextMenu(new Point(0, 0)));
            RunUI(() =>
                {
                    Assert.IsFalse(SkylineWindow.SetStandardTypeConextMenuItem.Visible);
                    SkylineWindow.ContextMenuTreeNode.Close();
                });
            // Select first peptide, which is not actually an iRT standard
            SelectNode(SrmDocument.Level.Molecules, 0);
            RunUI(() => SkylineWindow.ShowTreeNodeContextMenu(new Point(0, 0)));
            RunUI(() =>
            {
                Assert.IsTrue(SkylineWindow.SetStandardTypeConextMenuItem.Visible);
                SkylineWindow.ContextMenuTreeNode.Close();
            });
            // CONSIDER: Would have been nice to validate the contents of the Set Standard Type menu
            //           but it was difficult to get it showing with the Opening event handler firing

            // Set QC type
            RunUI(() => SetPeptideStandardType(1, 0, 3, PeptideDocNode.STANDARD_TYPE_QC));

            // Set back to none
            RunUI(() => SetPeptideStandardType(1, 0, 3, null));

            // Set to decoys should fail
            if (!AsSmallMolecules)
            {
                RunUI(() => SetPeptideStandardType(SkylineWindow.DocumentUI.MoleculeGroupCount - 1, 0, 5,
                    PeptideDocNode.STANDARD_TYPE_NORMALIZAITON, false));
            }
            // Set Normalization type
            RunUI(() => SetPeptideStandardType(1, 0, 3, PeptideDocNode.STANDARD_TYPE_NORMALIZAITON));
            RunUI(() =>
            {
                AreaReplicateGraphPane pane;
                Assert.IsTrue(SkylineWindow.GraphPeakArea.TryGetGraphPane(out pane));
                Assert.IsTrue(pane.YAxis.Scale.Max > 3e+6);
                Assert.IsTrue(pane.YAxis.Title.Text.StartsWith(Resources.AreaReplicateGraphPane_UpdateGraph_Peak_Area));

                SkylineWindow.NormalizeAreaGraphTo(AreaNormalizeToView.area_global_standard_view);
            });
            WaitForGraphs();

            var peptidePath = SkylineWindow.Document.GetPathTo((int) SrmDocument.Level.Molecules, SELECTED_PEPTIDE_INDEX);
            RunUI(() =>
            {
                AreaReplicateGraphPane pane;
                Assert.IsTrue(SkylineWindow.GraphPeakArea.TryGetGraphPane(out pane));
                Assert.IsTrue(pane.YAxis.Scale.Max < 0.12);
                Assert.IsTrue(pane.YAxis.Scale.Max > 0.09);
                Assert.IsTrue(pane.YAxis.Title.Text.StartsWith(Resources.AreaReplicateGraphPane_UpdateGraph_Peak_Area_Ratio_To_Global_Standards));
                Assert.AreEqual(5, SkylineWindow.GraphPeakArea.CurveCount);

                // Select a peptide with both light and heavy precursors
                SkylineWindow.SelectedPath = peptidePath;
                // Make it a QC peptide to have one in the StandardType report column later
                SkylineWindow.SetStandardType(PeptideDocNode.STANDARD_TYPE_QC);
            });
            WaitForGraphs();

            if (AsSmallMolecules)
                return;  // No small molecule ion labels in 3.1

            RunUI(() =>
            {
                // Should stay ratio to global standards
                AreaReplicateGraphPane pane;
                Assert.IsTrue(SkylineWindow.GraphPeakArea.TryGetGraphPane(out pane));
                double yMax = pane.YAxis.Scale.Max;
                Assert.IsTrue(0.14 <= yMax && yMax  <= 0.22, string.Format("{0} not between 0.14 and 0.22", yMax));  // Not L10N
                Assert.IsTrue(pane.YAxis.Title.Text.StartsWith(Resources.AreaReplicateGraphPane_UpdateGraph_Peak_Area_Ratio_To_Global_Standards));
                Assert.AreEqual(2, SkylineWindow.GraphPeakArea.CurveCount);
            });

            if (IsEnableLiveReports)
            {
                TestLiveResultsGrid();
            }
            else
            {
                TestOldResultsGrid();
            }

            // Open the MultiLabel.sky file
            string documentPath2 = TestFilesDir.GetTestPath("MultiLabel.sky");
            RunUI(() => SkylineWindow.OpenFile(documentPath2));
            WaitForDocumentLoaded();
            RunUI(() => SkylineWindow.SetStandardType(PeptideDocNode.STANDARD_TYPE_NORMALIZAITON));
        }

        private void TestLiveResultsGrid()
        {
            var ratioColumnLight = PropertyPath.Root.Property(RatioPropertyDescriptor.MakePropertyName(RatioPropertyDescriptor.RATIO_GS_PREFIX, IsotopeLabelType.light));
            var ratioColumnHeavy = PropertyPath.Root.Property(RatioPropertyDescriptor.MakePropertyName(RatioPropertyDescriptor.RATIO_GS_PREFIX, IsotopeLabelType.heavy));
            var ratioColumnPrec = PropertyPath.Root.Property(RatioPropertyDescriptor.MakePropertyName(RatioPropertyDescriptor.RATIO_GS_PREFIX, new IsotopeLabelType[0]));
            var ratioColumnTran = PropertyPath.Root.Property(RatioPropertyDescriptor.MakePropertyName(RatioPropertyDescriptor.RATIO_GS_PREFIX, new IsotopeLabelType[0]));
            var resultsGridForm = ShowDialog<LiveResultsGrid>(() => SkylineWindow.ShowResultsGrid(true));

            var resultsGrid = resultsGridForm.DataGridView;
            AddResultGridColumns(resultsGridForm, ratioColumnLight, ratioColumnHeavy);
            var lightValues = new List<double>();
            var heavyValues = new List<double>();
            RunUI(() =>
            {
                // Make sure cells have values
                foreach (DataGridViewRow row in resultsGrid.Rows)
                {
                    double lightValue = (double)row.Cells[resultsGridForm.FindColumn(ratioColumnLight).Index].Value;
                    Assert.IsFalse(string.IsNullOrEmpty(lightValue.ToString(CultureInfo.CurrentCulture)));
                    lightValues.Add(lightValue);
                    double heavyValue = (double)row.Cells[resultsGridForm.FindColumn(ratioColumnHeavy).Index].Value;
                    Assert.IsFalse(string.IsNullOrEmpty(heavyValue.ToString(CultureInfo.CurrentCulture)));
                    heavyValues.Add(heavyValue);
                }

                // Select the first precursor
                SkylineWindow.SelectedNode.Expand();
                SkylineWindow.SequenceTree.SelectedNode = SkylineWindow.SelectedNode.NextVisibleNode;
            });
            WaitForGraphs();
            var ratioColumnPrecursorLight = PropertyPath.Root.Property("PeptideResult").Concat(ratioColumnLight);
            var ratioColumnPrecursorHeavy = PropertyPath.Root.Property("PeptideResult").Concat(ratioColumnHeavy);
            AddResultGridColumns(resultsGridForm, ratioColumnPrecursorLight, ratioColumnPrecursorHeavy, ratioColumnPrec);
            RunUI(() =>
            {
                // Make sure precursor ratios match the values shown for the peptide
                for (int i = 0; i < resultsGrid.Rows.Count; i++)
                    Assert.AreEqual(lightValues[i], resultsGrid.Rows[i].Cells[resultsGridForm.FindColumn(ratioColumnPrecursorLight).Index].Value);
                SkylineWindow.SequenceTree.SelectedNode = SkylineWindow.SelectedNode.NextVisibleNode;
                for (int i = 0; i < resultsGrid.Rows.Count; i++)
                    Assert.AreEqual(heavyValues[i], resultsGrid.Rows[i].Cells[resultsGridForm.FindColumn(ratioColumnPrecursorHeavy).Index].Value);

                // Select the first transtion of the heavy precursor
                SkylineWindow.SelectedNode.Expand();
                SkylineWindow.SequenceTree.SelectedNode = SkylineWindow.SelectedNode.NextVisibleNode;
            });
            WaitForGraphs();
            
            AddResultGridColumns(resultsGridForm, ratioColumnTran);

            RunUI(() =>
            {
                // Make sure transition ratios are present
                foreach (DataGridViewRow row in resultsGrid.Rows)
                {
                    double lightValue = (double)row.Cells[resultsGridForm.FindColumn(ratioColumnTran).Index].Value;
                    Assert.IsFalse(string.IsNullOrEmpty(lightValue.ToString(CultureInfo.CurrentCulture)));
                }
            });

            var exportReportDlg = ShowDialog<ExportLiveReportDlg>(SkylineWindow.ShowExportReportDialog);
            var editReportListDlg = ShowDialog<EditListDlg<SettingsListBase<ReportOrViewSpec>, ReportOrViewSpec>>(exportReportDlg.EditList);
            var viewEditor = ShowDialog<ViewEditor>(editReportListDlg.AddItem);

            var columnsToAdd = new[]
                    {
                        // Not L10N
                        PropertyPath.Parse("Proteins!*.Peptides!*.Sequence"),
                        PropertyPath.Parse("Proteins!*.Peptides!*.StandardType"),
                        PropertyPath.Parse("Proteins!*.Peptides!*.Precursors!*.Charge"),
                        PropertyPath.Parse("Proteins!*.Peptides!*.Precursors!*.IsotopeLabelType"),
                        PropertyPath.Parse("Proteins!*.Peptides!*.Results!*.Value").Concat(ratioColumnLight),
                        PropertyPath.Parse("Proteins!*.Peptides!*.Results!*.Value").Concat(ratioColumnHeavy),
                        PropertyPath.Parse("Proteins!*.Peptides!*.Precursors!*.Results!*.Value").Concat(ratioColumnPrec),
                        PropertyPath.Parse("Proteins!*.Peptides!*.Precursors!*.Transitions!*.FragmentIon"),
                        PropertyPath.Parse("Proteins!*.Peptides!*.Precursors!*.Transitions!*.Results!*.Value").Concat(ratioColumnTran),
                    };
            RunUI(() =>
            {
                foreach (PropertyPath id in columnsToAdd)
                {
                    Assert.IsTrue(viewEditor.ChooseColumnsTab.TrySelect(id), string.Format("Failed attempting to add {0} to a report", id));
                    viewEditor.ChooseColumnsTab.AddSelectedColumn();
                }
            });
            var previewReportDlg = ShowDialog<DocumentGridForm>(viewEditor.ShowPreview);

            RunUI(() =>
            {
                var doc = SkylineWindow.DocumentUI;
                int countReplicates = doc.Settings.MeasuredResults.Chromatograms.Count;
                Assert.AreEqual(doc.MoleculeTransitionCount * countReplicates, previewReportDlg.RowCount);
                Assert.AreEqual(columnsToAdd.Length, previewReportDlg.ColumnCount);
                int iStandardType = previewReportDlg.FindColumn(PropertyPath.Parse("Precursor.Peptide.StandardType")).Index;
                int iLabelType = previewReportDlg.FindColumn(PropertyPath.Parse("Precursor.IsotopeLabelType")).Index;
                int iLightRatio = previewReportDlg.FindColumn(
                        PropertyPath.Parse("Results!*.Value.PrecursorResult.PeptideResult").Concat(ratioColumnLight))
                        .Index;
                int iHeavyRatio = previewReportDlg.FindColumn(
                        PropertyPath.Parse("Results!*.Value.PrecursorResult.PeptideResult").Concat(ratioColumnHeavy))
                        .Index;
                int iPrecRatio = previewReportDlg.FindColumn(
                        PropertyPath.Parse("Results!*.Value.PrecursorResult").Concat(ratioColumnPrec)).Index;
                int iTranRatio =
                    previewReportDlg.FindColumn(PropertyPath.Parse("Results!*.Value").Concat(ratioColumnTran)).Index;
                int iRow = 0, iPeptide = 0;
                foreach (var nodePep in doc.Molecules)
                {
                    foreach (var nodeGroup in nodePep.TransitionGroups)
                    {
                        for (int t = 0; t < nodeGroup.TransitionCount; t++)
                        {
                            for (int i = 0; i < countReplicates; i++)
                            {
                                var row = previewReportDlg.DataGridView.Rows[iRow++];
                                string standardTypeExpected = nodePep.GlobalStandardType;
                                var standardTypeActual = row.Cells[iStandardType].Value;
                                if (standardTypeExpected == null)
                                {
                                    if (standardTypeActual != null)
                                        Assert.IsNull(standardTypeActual,
                                            string.Format("Non-null standard type {0} found when not expected",
                                                standardTypeActual));
                                }
                                else
                                {
                                    if (!Equals(standardTypeExpected, standardTypeActual))
                                    {
                                        Assert.AreEqual(standardTypeExpected, standardTypeActual);
                                    }
                                }
                                float peptideLightRatio = (float)(double)row.Cells[iLightRatio].Value;
                                // Light ration never empty
                                Assert.IsTrue(peptideLightRatio > 0);
                                float peptideHeavyRatio = (float)(double)row.Cells[iHeavyRatio].Value;
                                // Heavy ratio empty when peptide has only a light precursor
                                Assert.IsTrue(nodePep.TransitionGroupCount == 1
                                    ? peptideHeavyRatio == 0
                                    : peptideHeavyRatio > 0 && peptideHeavyRatio != peptideLightRatio);
                                string labelType = row.Cells[iLabelType].Value.ToString();
                                float precursorRatio = (float)(double)row.Cells[iPrecRatio].Value;
                                if (string.Equals(labelType, IsotopeLabelType.light.Name))
                                    Assert.AreEqual(peptideLightRatio, precursorRatio);
                                else
                                    Assert.AreEqual(peptideHeavyRatio, precursorRatio);
                                if (iPeptide == SELECTED_PEPTIDE_INDEX)
                                {
                                    Assert.AreEqual(lightValues[i], peptideLightRatio);
                                    Assert.AreEqual(heavyValues[i], peptideHeavyRatio);
                                }

                                float transitionRatio = (float)(double)row.Cells[iTranRatio].Value;
                                Assert.IsTrue(transitionRatio < precursorRatio);
                            }
                        }
                    }
                    iPeptide++;
                }
            });

            OkDialog(previewReportDlg, previewReportDlg.Close);
            OkDialog(viewEditor, viewEditor.CancelButton.PerformClick);
            OkDialog(editReportListDlg, editReportListDlg.OkDialog);
            OkDialog(exportReportDlg, exportReportDlg.CancelClick);
        }

        private void TestOldResultsGrid()
        {
            var ratioColumnLight = RatioPropertyAccessor.PeptideRatioProperty(IsotopeLabelType.light, null);
            var ratioColumnHeavy = RatioPropertyAccessor.PeptideRatioProperty(IsotopeLabelType.heavy, null);
            var ratioColumnPrec = RatioPropertyAccessor.PrecursorRatioProperty(null);
            var ratioColumnTran = RatioPropertyAccessor.TransitionRatioProperty(null);

            var resultsGridForm = ShowDialog<ResultsGridForm>(() => SkylineWindow.ShowResultsGrid(true));
            var resultsGrid = resultsGridForm.ResultsGrid;
            AddResultGridColumns(resultsGridForm, ratioColumnLight, ratioColumnHeavy);

            var lightValues = new List<float>();
            var heavyValues = new List<float>();
            RunUI(() =>
            {
                // Make sure cells have values
                foreach (DataGridViewRow row in resultsGrid.Rows)
                {
                    float lightValue = (float) row.Cells[ratioColumnLight.ColumnName].Value;
                    Assert.IsFalse(string.IsNullOrEmpty(lightValue.ToString(CultureInfo.CurrentCulture)));
                    lightValues.Add(lightValue);
                    float heavyValue = (float) row.Cells[ratioColumnHeavy.ColumnName].Value;
                    Assert.IsFalse(string.IsNullOrEmpty(heavyValue.ToString(CultureInfo.CurrentCulture)));
                    heavyValues.Add(heavyValue);
                }

                // Select the first precursor
                SkylineWindow.SelectedNode.Expand();
                SkylineWindow.SequenceTree.SelectedNode = SkylineWindow.SelectedNode.NextVisibleNode;
            });
            WaitForGraphs();

            AddResultGridColumns(resultsGridForm, ratioColumnPrec);

            RunUI(() =>
            {
                // Make sure precursor ratios match the values shown for the peptide
                for (int i = 0; i < resultsGrid.Rows.Count; i++)
                    Assert.AreEqual(lightValues[i], resultsGrid.Rows[i].Cells[ratioColumnLight.ColumnName].Value);
                SkylineWindow.SequenceTree.SelectedNode = SkylineWindow.SelectedNode.NextVisibleNode;
                for (int i = 0; i < resultsGrid.Rows.Count; i++)
                    Assert.AreEqual(heavyValues[i], resultsGrid.Rows[i].Cells[ratioColumnHeavy.ColumnName].Value);

                // Select the first transtion of the heavy precursor
                SkylineWindow.SelectedNode.Expand();
                SkylineWindow.SequenceTree.SelectedNode = SkylineWindow.SelectedNode.NextVisibleNode;
            });
            WaitForGraphs();

            AddResultGridColumns(resultsGridForm, ratioColumnTran);

            RunUI(() =>
            {
                // Make sure transition ratios are present
                foreach (DataGridViewRow row in resultsGrid.Rows)
                {
                    float lightValue = (float) row.Cells[ratioColumnTran.ColumnName].Value;
                    Assert.IsFalse(string.IsNullOrEmpty(lightValue.ToString(CultureInfo.CurrentCulture)));
                }
            });
            var exportReportDlg = ShowDialog<ExportReportDlg>(SkylineWindow.ShowExportReportDialog);
            var editReportListDlg = ShowDialog<EditListDlg<SettingsListBase<ReportSpec>, ReportSpec>>(exportReportDlg.EditList);
            var pivotReportDlg = ShowDialog<PivotReportDlg>(editReportListDlg.AddItem);

            var columnsToAdd = new[]
                    {
                        // Not L10N
                        new Identifier("Peptides", "Sequence"),
                        new Identifier("Peptides", "StandardType"),
                        new Identifier("Peptides", "Precursors", "Charge"),
                        new Identifier("Peptides", "Precursors", "IsotopeLabelType"),
                        new Identifier("Peptides", "PeptideResults", ratioColumnLight.DisplayName),
                        new Identifier("Peptides", "PeptideResults", ratioColumnHeavy.DisplayName),
                        new Identifier("Peptides", "Precursors", "PrecursorResults", ratioColumnPrec.DisplayName),
                        new Identifier("Peptides", "Precursors", "Transitions", "FragmentIon"),
                        new Identifier("Peptides", "Precursors", "Transitions", "TransitionResults", ratioColumnTran.DisplayName),
                    };
            RunUI(() =>
            {
                foreach (Identifier id in columnsToAdd)
                {
                    Assert.IsTrue(pivotReportDlg.TrySelect(id), string.Format("Failed attempting to add {0} to a report", id));
                    pivotReportDlg.AddSelectedColumn();
                }
            });
            var previewReportDlg = ShowDialog<PreviewReportDlg>(pivotReportDlg.ShowPreview);

            RunUI(() =>
            {
                var doc = SkylineWindow.DocumentUI;
                int countReplicates = doc.Settings.MeasuredResults.Chromatograms.Count;
                Assert.AreEqual(doc.MoleculeTransitionCount*countReplicates, previewReportDlg.RowCount);
                Assert.AreEqual(columnsToAdd.Length, previewReportDlg.ColumnCount);
                var headerNames = previewReportDlg.ColumnHeaderNames.ToArray();
                int iStandardType = headerNames.IndexOf(name => Equals(name, "StandardType"));
                int iLabelType = headerNames.IndexOf(name => Equals(name, "IsotopeLabelType"));
                int iLightRatio = headerNames.IndexOf(name => Equals(name, ratioColumnLight.DisplayName));
                int iHeavyRatio = headerNames.IndexOf(name => Equals(name, ratioColumnHeavy.DisplayName));
                int iPrecRatio = headerNames.IndexOf(name => Equals(name, ratioColumnPrec.DisplayName));
                int iTranRatio = headerNames.IndexOf(name => Equals(name, ratioColumnTran.DisplayName));
                int iRow = 0, iPeptide = 0;
                foreach (var nodePep in doc.Molecules)
                {
                    foreach (var nodeGroup in nodePep.TransitionGroups)
                    {
                        for (int t = 0; t < nodeGroup.TransitionCount; t++)
                        {
                            for (int i = 0; i < countReplicates; i++)
                            {
                                var row = previewReportDlg.DataGridView.Rows[iRow++];
                                string standardTypeExpected = nodePep.GlobalStandardType;
                                var standardTypeActual = row.Cells[iStandardType].Value;
                                if (standardTypeExpected == null)
                                {
                                    if (standardTypeActual != null)
                                        Assert.IsNull(standardTypeActual, string.Format("Non-null standard type {0} found when not expected", standardTypeActual));
                                }
                                else
                                    Assert.AreEqual(standardTypeExpected, standardTypeActual);
                                float peptideLightRatio = (float)(double)row.Cells[iLightRatio].Value;
                                // Light ration never empty
                                Assert.IsTrue(peptideLightRatio > 0);
                                float peptideHeavyRatio = (float)(double)row.Cells[iHeavyRatio].Value;
                                // Heavy ratio empty when peptide has only a light precursor
                                Assert.IsTrue(nodePep.TransitionGroupCount == 1
                                    ? peptideHeavyRatio == 0
                                    : peptideHeavyRatio > 0 && peptideHeavyRatio != peptideLightRatio);
                                string labelType = row.Cells[iLabelType].Value.ToString();
                                float precursorRatio = (float)(double)row.Cells[iPrecRatio].Value;
                                if (string.Equals(labelType, IsotopeLabelType.light.Name))
                                    Assert.AreEqual(peptideLightRatio, precursorRatio);
                                else
                                    Assert.AreEqual(peptideHeavyRatio, precursorRatio);
                                if (iPeptide == SELECTED_PEPTIDE_INDEX)
                                {
                                    Assert.AreEqual(lightValues[i], peptideLightRatio);
                                    Assert.AreEqual(heavyValues[i], peptideHeavyRatio);
                                }

                                float transitionRatio = (float)(double)row.Cells[iTranRatio].Value;
                                Assert.IsTrue(transitionRatio < precursorRatio);
                            }
                        }
                    }
                    iPeptide++;
                }
            });

            OkDialog(previewReportDlg, previewReportDlg.OkDialog);
            OkDialog(pivotReportDlg, pivotReportDlg.CancelButton.PerformClick);
            OkDialog(editReportListDlg, editReportListDlg.OkDialog);
            OkDialog(exportReportDlg, exportReportDlg.CancelClick);
        }

        private static void AddResultGridColumns(LiveResultsGrid resultsGridForm, params PropertyPath[] ratioPropertyNames)
        {
            RunDlg<ViewEditor>(resultsGridForm.NavBar.CustomizeView, viewEditor =>
            {
                foreach (var ratioPropertyName in ratioPropertyNames)
                {
                    Assert.IsTrue(viewEditor.ChooseColumnsTab.TrySelect(ratioPropertyName));
                    viewEditor.ChooseColumnsTab.AddSelectedColumn();
                }
                viewEditor.OkDialog();
            });
            WaitForConditionUI(() => resultsGridForm.IsComplete);
        }

        private static void AddResultGridColumns(ResultsGridForm resultsGridForm, params RatioPropertyAccessor.RatioPropertyName[] ratioPropertyNames)
        {
            RunDlg<ColumnChooser>(resultsGridForm.ChooseColumns, chooseColumnsDlg =>
                {
                    foreach (var ratioPropertyName in ratioPropertyNames)
                    {
                        chooseColumnsDlg.CheckedListBox.SetItemChecked(
                            chooseColumnsDlg.CheckedListBox.Items.IndexOf(ratioPropertyName.HeaderText), true);
                    }
                    chooseColumnsDlg.AcceptButton.PerformClick();
                });
        }

        private void SetPeptideStandardType(int protindex, int pepStartIndex, int pepCount, string standardType, bool success = true)
        {
            var qcPeps = SkylineWindow.SequenceTree.Nodes[protindex];
            SelectRange(qcPeps.Nodes[pepStartIndex], qcPeps.Nodes[pepStartIndex + pepCount - 1]);
            SkylineWindow.SetStandardType(standardType);

            var docChanged = SkylineWindow.DocumentUI;
            ValidateStandardType(docChanged, protindex, pepStartIndex, pepCount, standardType, success);

            var docRound = AssertEx.RoundTrip(docChanged);
            ValidateStandardType(docRound, protindex, pepStartIndex, pepCount, standardType, success);
        }

        private static void ValidateStandardType(SrmDocument docChanged, int protindex, int pepStartIndex, int pepCount,
                                                 string standardType, bool success)
        {
            var pepsChanged = docChanged.MoleculeGroups.ElementAt(protindex).Molecules;
            Assert.IsTrue(pepsChanged.Skip(pepStartIndex).Take(pepCount).All(nodePep =>
                Equals(nodePep.GlobalStandardType, standardType)) == success);
        }

        private void SelectRange(TreeNode nodeStart, TreeNode nodeEnd)
        {
            var tree = SkylineWindow.SequenceTree;
            tree.KeysOverride = Keys.None;
            tree.SelectedNode = nodeStart;
            tree.KeysOverride = Keys.Shift;
            tree.SelectedNode = nodeEnd;
            tree.KeysOverride = Keys.None;
        }
    }
}