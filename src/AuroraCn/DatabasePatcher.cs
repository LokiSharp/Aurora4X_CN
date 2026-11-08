using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace AuroraCn
{
    internal static class DatabasePatcher
    {
        internal static void Write(string inputPath, string outputPath, TranslationTables tables)
        {
            File.Copy(inputPath, outputPath, true);
            SQLitePCL.Batteries_V2.Init();
            int updated = 0;
            int skipped = 0;
            using (var conn = new SqliteConnection("Data Source=" + outputPath))
            {
                conn.Open();
                var columns = LoadColumns(conn);
                using (var tx = conn.BeginTransaction())
                {
                    foreach (FieldRow row in tables.Fields)
                    {
                        HashSet<string> cols;
                        if (!columns.TryGetValue(row.Table, out cols) || !cols.Contains(row.Column) || !SafeIdent(row.Table) || !SafeIdent(row.Column))
                        {
                            skipped++;
                            continue;
                        }
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "UPDATE \"" + row.Table + "\" SET \"" + row.Column + "\" = $zh WHERE \"" + row.Column + "\" = $en";
                            cmd.Parameters.AddWithValue("$zh", row.Chinese);
                            cmd.Parameters.AddWithValue("$en", row.English);
                            updated += cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
            Console.WriteLine("db updated=" + updated + " skipped=" + skipped);
        }

        private static Dictionary<string, HashSet<string>> LoadColumns(SqliteConnection conn)
        {
            var columns = new Dictionary<string, HashSet<string>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using (var reader = cmd.ExecuteReader())
                {
                    var names = new List<string>();
                    while (reader.Read())
                        names.Add(reader.GetString(0));
                    foreach (string name in names)
                    {
                        if (!SafeIdent(name))
                            continue;
                        var cols = new HashSet<string>();
                        using (var info = conn.CreateCommand())
                        {
                            info.CommandText = "PRAGMA table_info(\"" + name + "\")";
                            using (var infoReader = info.ExecuteReader())
                            {
                                while (infoReader.Read())
                                    cols.Add(infoReader.GetString(1));
                            }
                        }
                        columns[name] = cols;
                    }
                }
            }
            return columns;
        }

        private static bool SafeIdent(string name)
        {
            if (name.Length == 0)
                return false;
            foreach (char c in name)
            {
                if (c != '_' && !char.IsLetterOrDigit(c))
                    return false;
            }
            return true;
        }
    }
}
