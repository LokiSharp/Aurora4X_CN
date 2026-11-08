using System;
using System.IO;

namespace AuroraCn
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string gameDir = args.Length > 0 ? Path.GetFullPath(args[0]) : FindGameDirectory();
            if (gameDir == null || !File.Exists(Path.Combine(gameDir, "Aurora.exe")))
            {
                Console.Error.WriteLine("Aurora.exe was not found. Pass the game directory as the first argument.");
                return 2;
            }
            string locDir = Path.Combine(gameDir, "localization");
            if (!Directory.Exists(locDir))
            {
                Console.Error.WriteLine("localization directory was not found under " + gameDir);
                return 2;
            }
            var tables = TranslationTables.Load(locDir);
            string exeOut = Path.Combine(gameDir, "AuroraCN.exe");
            string dbOut = Path.Combine(gameDir, "AuroraCN.db");
            ExePatcher.Write(Path.Combine(gameDir, "Aurora.exe"), exeOut, tables);
            string config = Path.Combine(gameDir, "Aurora.exe.config");
            if (File.Exists(config))
                File.Copy(config, Path.Combine(gameDir, "AuroraCN.exe.config"), true);
            DatabasePatcher.Write(Path.Combine(gameDir, "AuroraDB.db"), dbOut, tables);
            Console.WriteLine("wrote " + exeOut);
            Console.WriteLine("wrote " + dbOut);
            return 0;
        }

        private static string FindGameDirectory()
        {
            DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Aurora.exe")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
