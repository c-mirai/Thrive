﻿namespace Saving
{
    using System;
    using System.Collections.Generic;
    using Godot;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public interface ISaveUpgradeStep
    {
        /// <summary>
        ///   Performs a save upgrade. Throws exceptions on failure.
        /// </summary>
        /// <param name="saveInfo">Loaded save info from inputSave</param>
        /// <param name="inputSave">Save name that can be used to read the save data</param>
        /// <param name="outputSave">Target name of the upgraded save</param>
        /// <returns>The new version of the upgraded save</returns>
        string PerformUpgrade(SaveInformation saveInfo, string inputSave, string outputSave);

        /// <summary>
        ///   Calculates what the version of save would be if PerformUpgrade is ran on it
        /// </summary>
        /// <param name="saveInfo">The save info to inspect</param>
        /// <returns>The version after upgrade, null if can't upgrade</returns>
        string VersionAfterUpgrade(SaveInformation saveInfo);
    }

    public static class SaveUpgradeSteps
    {
        private static readonly Dictionary<string, ISaveUpgradeStep> StoredSaveUpgradeSteps =
            InitializeSaveUpgradeSteps();

        public static IReadOnlyDictionary<string, ISaveUpgradeStep> SupportedUpgrades => StoredSaveUpgradeSteps;

        public static ISaveUpgradeStep GetUpgradeStepForVersion(string version)
        {
            if (!SupportedUpgrades.TryGetValue(version, out ISaveUpgradeStep step))
                return null;

            return step;
        }

        private static Dictionary<string, ISaveUpgradeStep> InitializeSaveUpgradeSteps()
        {
            // TODO: would it be useful to specify a range of versions an upgrader can upgrade to make it less error
            // prone to accidentally miss a version
            return new Dictionary<string, ISaveUpgradeStep>
            {
                { "0.5.4.0-rc1", new UpgradeJustVersionNumber("0.5.4.0") },
                { "0.5.4.0", new UpgradeStep054To055() },
                { "0.5.5.0-alpha", new UpgradeJustVersionNumber("0.5.5.0-rc1") },
                { "0.5.5.0-rc1", new UpgradeJustVersionNumber("0.5.5.0") },
            };
        }
    }

    internal class UpgradeStep054To055 : BaseRecursiveJSONWalkerStep
    {
        protected override string VersionAfter => "0.5.5.0-alpha";

        protected override void RecursivelyUpdateObjectProperties(JObject jObject)
        {
            base.RecursivelyUpdateObjectProperties(jObject);

            foreach (var entry in jObject.Properties())
            {
                if (entry.Name == "DespawnRadiusSqr")
                {
                    // This modifies the object so this is done in a separate loop that is broken when hit
                    GD.Print("Updating property name at ", entry.Path);
                    entry.Replace(new JProperty("DespawnRadiusSquared", entry.Value));
                    break;
                }
            }
        }

        protected override void CheckAndUpdateProperty(JProperty property)
        {
            if (property.Name.Contains("Membrane") || property.Name.Contains("membrane"))
            {
                if (property.Value.Type == JTokenType.String &&
                    property.Value.ToObject<string>() == "calcium_carbonate")
                {
                    GD.Print("Updating value at ", property.Path);
                    property.Value = "calciumCarbonate";
                }
            }
        }
    }

    internal abstract class BaseRecursiveJSONWalkerStep : BaseJSONUpgradeStep
    {
        protected override void PerformUpgradeOnJSON(JObject saveData)
        {
            RecursivelyUpdateObjectProperties(saveData);
        }

        protected virtual void RecursivelyUpdateObjectProperties(JObject jObject)
        {
            if (jObject == null)
                throw new JsonException("Null JSON object passed to looping properties");

            foreach (var entry in jObject.Properties())
            {
                RecursivelyUpdateValues(entry);
            }
        }

        protected abstract void CheckAndUpdateProperty(JProperty property);

        private void RecursivelyUpdateValues(JProperty property)
        {
            CheckAndUpdateProperty(property);

            if (property.Value.Type == JTokenType.Array)
            {
                var listObject = property.Value as JArray;
                if (listObject == null)
                    throw new JsonException("Child array convert to array type failed");

                foreach (var entry in listObject)
                {
                    if (entry.Type == JTokenType.Object)
                        RecursivelyUpdateObjectProperties(entry as JObject);
                }
            }

            if (property.Value.Type != JTokenType.Object)
                return;

            var valueObject = property.Value as JObject;
            if (valueObject == null)
                throw new JsonException("Child object convert to object type failed");

            RecursivelyUpdateObjectProperties(valueObject);
        }
    }

    internal abstract class BaseJSONUpgradeStep : ISaveUpgradeStep
    {
        protected abstract string VersionAfter { get; }

        public string PerformUpgrade(SaveInformation saveInfo, string inputSave, string outputSave)
        {
            var versionDifference = VersionUtils.Compare(VersionAfter, saveInfo.ThriveVersion);

            if (versionDifference == int.MaxValue)
                throw new Exception("Could not compare version in save to version it would be upgraded to");

            if (versionDifference <= 0)
            {
                throw new ArgumentException("This converter can't upgrade the provided save");
            }

            // SaveInformation is not used here as saveInfo is assumed to be up to date
            var (freshInfo, saveStructure, screenshot) = Save.LoadJSONStructureFromFile(inputSave);

            if (freshInfo.ThriveVersion != saveInfo.ThriveVersion)
                GD.PrintErr("Unexpected save version in freshly loaded save information");

            PerformUpgradeOnJSON(saveStructure);

            PerformUpgradeOnInfo(saveInfo);

            CopySaveInfoToStructure(saveStructure, saveInfo);

            // TODO: should the "Name" in saveStructure be updated? (there's a bigger need to update it when making the
            // backup file rather than here...)

            Save.WriteSaveJSONToFile(saveInfo, saveStructure, screenshot, outputSave);

            return VersionAfter;
        }

        public string VersionAfterUpgrade(SaveInformation saveInfo)
        {
            return VersionAfter;
        }

        protected virtual void PerformUpgradeOnInfo(SaveInformation saveInformation)
        {
            saveInformation.ThriveVersion = VersionAfter;

            // Update the ID of the save as it is in practice save with different content
            saveInformation.ID = Guid.NewGuid();
        }

        protected abstract void PerformUpgradeOnJSON(JObject saveData);

        private void CopySaveInfoToStructure(JObject saveData, SaveInformation saveInfo)
        {
            var info = saveData[nameof(Save.Info)];

            foreach (var property in BaseThriveConverter.PropertiesOf(saveInfo))
            {
                info[property.Name] = JToken.FromObject(property.GetValue(saveInfo));
            }
        }
    }

    /// <summary>
    ///   Just updates the save version in a save file. Can be used to bring a file up to date if the actual save data
    ///   doesn't need any changes
    /// </summary>
    internal class UpgradeJustVersionNumber : BaseJSONUpgradeStep
    {
        public UpgradeJustVersionNumber(string versionToSet)
        {
            VersionAfter = versionToSet;
        }

        protected override string VersionAfter { get; }

        protected override void PerformUpgradeOnJSON(JObject saveData)
        {
            // Nothing is actually needed to be done here as the base class wil already update save info to the new
            // version and copy that to saveData
        }
    }
}
