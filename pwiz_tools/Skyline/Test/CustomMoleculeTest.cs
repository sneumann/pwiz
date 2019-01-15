﻿/*
 * Original author: Nicholas Shulman <nicksh .at. u.washington.edu>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2018 University of Washington - Seattle, WA
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using pwiz.Skyline.Model;
using pwiz.Skyline.Model.Lib;
using pwiz.SkylineTestUtil;

namespace pwiz.SkylineTest
{
    [TestClass]
    public class CustomMoleculeTest : AbstractUnitTest
    {
        [TestMethod]
        public void TestCustomMoleculeToTSV()
        {
            var moleculeAccessionNumbers = new MoleculeAccessionNumbers(new Dictionary<string, string>
            {
                {MoleculeAccessionNumbers.TagCAS, "MyCAS"},
                {MoleculeAccessionNumbers.TagHMDB, "MyHMDB"},
                {MoleculeAccessionNumbers.TagInChI, "MyInChI"},
                { MoleculeAccessionNumbers.TagSMILES, "MySmiles"}
            });
            var smallMoleculeLibraryAttributes = 
                SmallMoleculeLibraryAttributes.Create("MyMolecule", "H2O", "MyInChiKey", moleculeAccessionNumbers.GetNonInChiKeys());
            var customMolecule = new CustomMolecule(smallMoleculeLibraryAttributes);
            var target = new Target(customMolecule);
            var serializableString = target.ToSerializableString();

            var roundTrip = Target.FromSerializableString(serializableString);
            Assert.AreEqual(target, roundTrip);
            Assert.AreEqual(customMolecule, roundTrip.Molecule);
            Assert.AreEqual(customMolecule.AccessionNumbers, roundTrip.Molecule.AccessionNumbers);
        }
    }
}
