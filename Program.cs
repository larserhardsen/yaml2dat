
using Sitecore.Data.DataProviders.ReadOnly.Protobuf.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Rainbow.Storage;
using Rainbow.Storage.Yaml;
using System.Xml;
using CommandLine;
using Rainbow.Filtering;
using Rainbow.Model;

namespace yaml2dat
{
    public class Options
    {
        [Option('p', "YamlPath", Required = true, HelpText = "Path to the yaml files")]
        public string YamlPath { get; set; }

        [Option('o', "OutputFile", Required = true, HelpText = "Path to the output file")]
        public string OutputFile { get; set; }
    }

    public class Program
    {
        private static readonly List<string> log = new List<string>();

        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args).WithParsed(RunOptions);
        }

        private static void RunOptions(Options options)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            string path = options.YamlPath;
            try
            {
                path = new DirectoryInfo(options.YamlPath).FullName;
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"I don't understand the path {path}");
                return;
            }

            CreateDatFile(path, options.OutputFile);

            stopwatch.Stop();
            log.Add($"Elapsed time: {stopwatch.ElapsedMilliseconds / 1000} seconds");

            foreach (string text in log)
            {
                Console.WriteLine(text);
            }
        }

        private static void CreateDatFile(string physicalRootPath, string outputFileName)
        {
            ItemsData itemsData = new ItemsData();
            XmlElement node = new XmlDocument().CreateElement("node");

            SerializationFileSystemTree tree = new SerializationFileSystemTree("Yaml2dat",
                "/sitecore",
                "master",
                physicalRootPath,
                new YamlSerializationFormatter(node, new ConfigurationFieldFilter(node)), false);

            List<IItemData> snapshot = tree.GetSnapshot().ToList();
            log.Add($"Found {snapshot.Count} items to process");

            foreach (IItemData item in snapshot)
            {
                AddItem(item, itemsData);
            }

            using FileStream fileStream = new FileInfo(outputFileName).Create();
            ProtoBuf.Serializer.Serialize(fileStream, itemsData);
            log.Add($"Created file: {outputFileName}");
        }

        private static void AddItem(IItemData item, ItemsData itemsData)
        {
            if (itemsData.Definitions.ContainsKey(item.Id))
            {
                log.Add($"Skipping yaml for path {item.Path} as the itemId already has been added");
                return;
            }

            itemsData.Definitions.Add(item.Id, new ItemRecord
            {
                ID = item.Id,
                MasterID = item.BranchId,
                Name = item.Name,
                ParentID = item.ParentId,
                TemplateID = item.TemplateId
            });

            itemsData.SharedData.Add(item.Id, item.SharedFields.ToDictionary(key => key.FieldId, value => FixValue(value.Value)));

            // extract the versioned fields
            Dictionary<string, VersionsData> languageAndVersions = new Dictionary<string, VersionsData>();
            foreach (IItemVersion version in item.Versions)
            {
                if (!languageAndVersions.ContainsKey(version.Language.ToString()))
                {
                    languageAndVersions.Add(version.Language.ToString(), new VersionsData());
                }

                VersionsData versionData = languageAndVersions[version.Language.ToString()];
                versionData.Add(version.VersionNumber, PopulateFieldsData(version));
            }

            // add the versioned fields
            ItemLanguagesData languageFields = new ItemLanguagesData();
            foreach (KeyValuePair<string, VersionsData> pair in languageAndVersions)
            {
                languageFields.Add(pair.Key, pair.Value);
            }

            // add the unversioned fields (hardcoded to version 0)
            foreach (IItemLanguage language in item.UnversionedFields)
            {
                VersionsData versionsData = languageFields[language.Language.ToString()];
                if (versionsData == null)
                {
                    languageFields.Add(language.Language.ToString(), new VersionsData());
                    versionsData = languageFields[language.Language.ToString()];
                }

                versionsData.Add(0, PopulateFieldsData(language));
            }

            itemsData.LanguageData.Add(item.Id, languageFields);
        }

        private static FieldsData PopulateFieldsData(IItemLanguage language)
        {
            FieldsData fieldsData = new FieldsData();
            foreach (IItemFieldValue fieldValue in language.Fields)
            {
                fieldsData.Add(fieldValue.FieldId, fieldValue.Value);
            }

            return fieldsData;
        }

        private static string FixValue(string value)
        {
            // this is an attempt to fix issues when converting from yaml to fields with multiple Sitecore IDs, probably because of using the Rainbow engine instead of the Sitecore yaml serialization logic.
            // TODO: find better way of fixing this!
            if (value.StartsWith("{") && value.Contains("}\r\n{") && value.EndsWith("}"))
            {
                return value.Replace("}\r\n{", "}|{");
            }

            return value;
        }
    }
}
