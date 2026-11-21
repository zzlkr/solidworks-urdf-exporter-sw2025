using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace SW2URDF.URDFExport
{
    // No live COM references and no writes to the assembly. Incomplete trees are valid drafts.
    [DataContract]
    public sealed class ExportDraft
    {
        [DataMember] public int Version = 1;
        [DataMember] public string AssemblyPath;
        [DataMember] public string ConfigurationXml;
        [DataMember] public string Checksum;
        [DataMember] public DateTime SavedUtc;
        [DataMember] public int[] SelectedNode = new int[0];
        [DataMember] public bool[] ComputeOptions = new[] { true, true, true, true };
        [DataMember] public string CsvLabel;
        [DataMember] public bool Committed;
        [DataMember] public Dictionary<string, string> ComponentNames = new Dictionary<string, string>();
    }

    public sealed class DraftStore
    {
        private readonly string directory;
        public DraftStore(string directory) { this.directory = directory; }
        public static DraftStore Default => new DraftStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SW2URDF", "Drafts"));

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Save the assembly once before using draft recovery.");
            return Path.GetFullPath(path).ToUpperInvariant();
        }

        public string GetPath(string assemblyPath) => Path.Combine(directory, Hash(Normalize(assemblyPath)) + ".xml");

        private static string Hash(string value)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        public void Save(ExportDraft draft)
        {
            if (draft == null || string.IsNullOrWhiteSpace(draft.ConfigurationXml))
                throw new InvalidDataException("An empty snapshot must never overwrite a draft.");
            // Validate before replacing any recovery file.
            if (!ConfigurationSerialization.IsValidConfiguration(draft.ConfigurationXml))
                throw new InvalidDataException("The draft tree could not be read back.");
            string target = GetPath(draft.AssemblyPath);
            draft.Checksum = Hash(draft.ConfigurationXml);
            draft.SavedUtc = DateTime.UtcNow;
            Directory.CreateDirectory(directory);
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractSerializer(typeof(ExportDraft)).WriteObject(stream, draft);
                    stream.Flush(true);
                }
                Read(temp, draft.AssemblyPath); // Verify the on-disk document before committing.
                if (File.Exists(target))
                {
                    // Do not rotate a corrupt primary over a known-good backup.
                    bool primaryValid = true;
                    try { Read(target, draft.AssemblyPath); }
                    catch (Exception e) when (IsReadError(e)) { primaryValid = false; }
                    File.Replace(temp, target, primaryValid ? target + ".bak" : null);
                }
                else File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static bool IsReadError(Exception e) => e is IOException || e is InvalidDataException || e is SerializationException ||
            e is XmlException || e is UnauthorizedAccessException || e is ArgumentException;

        private static ExportDraft Read(string file, string assemblyPath)
        {
            using (XmlReader reader = XmlReader.Create(file, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 64 * 1024 * 1024
            }))
            {
                ExportDraft draft = (ExportDraft)new DataContractSerializer(typeof(ExportDraft)).ReadObject(reader);
                if (draft == null || draft.Version != 1 ||
                    Normalize(draft.AssemblyPath) != Normalize(assemblyPath) ||
                    string.IsNullOrWhiteSpace(draft.ConfigurationXml) || draft.Checksum != Hash(draft.ConfigurationXml) ||
                    !ConfigurationSerialization.IsValidConfiguration(draft.ConfigurationXml))
                    throw new InvalidDataException("Draft identity, checksum or tree validation failed.");
                return draft;
            }
        }

        public ExportDraft Load(string assemblyPath, out string warning)
        {
            warning = null;
            string path = GetPath(assemblyPath);
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try { return Read(candidate, assemblyPath); }
                catch (Exception e) when (IsReadError(e))
                {
                    warning = "草稿文件读取失败，已尝试上一份备份。\r\n" + e.Message;
                }
            }
            return null;
        }

        // Called only after an explicit discard. Both generations must disappear together.
        public void Discard(string assemblyPath)
        {
            string path = GetPath(assemblyPath);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            if (File.Exists(path)) File.Delete(path);
        }

        public void SaveCommitted(string assemblyPath, string xml)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath)) return;
            // A later save from the detailed export form must supersede the earlier panel draft.
            ExportDraft draft = Load(assemblyPath, out string warning) ?? new ExportDraft { AssemblyPath = assemblyPath };
            draft.ConfigurationXml = xml;
            draft.Committed = true;
            Save(draft);
        }
    }
}
