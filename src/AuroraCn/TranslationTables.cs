using System;
using System.Collections.Generic;
using System.IO;

namespace AuroraCn
{
    internal sealed class TranslationTables
    {
        internal readonly Dictionary<string, string> Ui = new Dictionary<string, string>();
        internal readonly Dictionary<string, string> Display = new Dictionary<string, string>();
        internal readonly List<FieldRow> Fields = new List<FieldRow>();

        internal static TranslationTables Load(string locDir)
        {
            var tables = new TranslationTables();
            LoadPairs(Path.Combine(locDir, "exe", "ui.tsv"), 0, 1, 2, delegate (string en, string zh, string reason)
            {
                tables.Ui[en] = zh;
            });
            LoadPairs(Path.Combine(locDir, "exe", "display.tsv"), 0, 1, 2, delegate (string en, string zh, string reason)
            {
                if (!tables.Ui.ContainsKey(en))
                    tables.Display[en] = zh;
            });
            LoadPairs(Path.Combine(locDir, "db", "fields"), 2, 3, 4, delegate (string en, string zh, string reason, string table, string column)
            {
                tables.Fields.Add(new FieldRow(table, column, en, zh));
            });
            tables.Ui["Data Source=AuroraDB.db;Version=3;New=False;Compress=True;"] =
                "Data Source=AuroraCN.db;Version=3;New=False;Compress=True;";
            tables.Ui["AuroraDB.db"] = "AuroraCN.db";
            tables.Ui["AuroraDBSaveBackup.db"] = "AuroraCNSaveBackup.db";
            tables.Ui["AuroraDBPreviousSaveBackup.db"] = "AuroraCNPreviousSaveBackup.db";
            return tables;
        }

        private static void LoadPairs(string path, int enIndex, int zhIndex, int reasonIndex, Action<string, string, string> add)
        {
            LoadPairs(path, enIndex, zhIndex, reasonIndex, delegate (string en, string zh, string reason, string table, string column)
            {
                add(en, zh, reason);
            });
        }

        private static void LoadPairs(string path, int enIndex, int zhIndex, int reasonIndex, Action<string, string, string, string, string> add)
        {
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.GetFiles(path, "*.tsv"))
                    LoadPairs(file, enIndex, zhIndex, reasonIndex, add);
                return;
            }
            if (!File.Exists(path))
                return;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line.StartsWith("english\t") || line.StartsWith("table\t"))
                    continue;
                string[] parts = line.Split('\t');
                if (parts.Length <= reasonIndex)
                    continue;
                string reason = parts[reasonIndex];
                string en = Unesc(parts[enIndex]);
                string zh = Unesc(parts[zhIndex]);
                if (zh.Length == 0 || zh == en || reason.StartsWith("不翻译"))
                    continue;
                string table = enIndex >= 2 ? parts[0] : "";
                string column = enIndex >= 2 ? parts[1] : "";
                add(en, zh, reason, table, column);
            }
        }

        private static string Unesc(string text)
        {
            var chars = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\' || i + 1 >= text.Length)
                {
                    chars.Append(c);
                    continue;
                }
                char next = text[++i];
                if (next == 'n')
                    chars.Append('\n');
                else if (next == 'r')
                    chars.Append('\r');
                else if (next == 't')
                    chars.Append('\t');
                else if (next == '\\')
                    chars.Append('\\');
                else
                {
                    chars.Append('\\');
                    chars.Append(next);
                }
            }
            return chars.ToString();
        }
    }

    internal sealed class FieldRow
    {
        internal readonly string Table;
        internal readonly string Column;
        internal readonly string English;
        internal readonly string Chinese;

        internal FieldRow(string table, string column, string english, string chinese)
        {
            Table = table;
            Column = column;
            English = english;
            Chinese = chinese;
        }
    }
}
