using System;
using System.IO;

namespace AuroraCn
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            bool checkOnly = false;
            bool exeOnly = false;
            string gameArg = null;
            foreach (string arg in args)
            {
                if (arg == "--check")
                    checkOnly = true;
                else if (arg == "--exe-only")
                    exeOnly = true;
                else if (gameArg == null)
                    gameArg = arg;
            }
            string gameDir = gameArg != null ? Path.GetFullPath(gameArg) : FindGameDirectory();
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
            string originalExe = Path.Combine(gameDir, "Aurora.exe");
            string exeOut = Path.Combine(gameDir, "AuroraCN.exe");
            if (checkOnly)
            {
                if (!File.Exists(exeOut))
                {
                    Console.Error.WriteLine("AuroraCN.exe was not found. Build it before --check.");
                    return 2;
                }
                return Finish(originalExe, exeOut);
            }
            var tables = TranslationTables.Load(locDir);
            string exeTemp = exeOut + ".tmp";
            ExePatcher.Write(originalExe, exeTemp, tables);
            File.Copy(exeTemp, exeOut, true);
            File.Delete(exeTemp);
            string config = Path.Combine(gameDir, "Aurora.exe.config");
            if (File.Exists(config))
                File.Copy(config, Path.Combine(gameDir, "AuroraCN.exe.config"), true);
            Console.WriteLine("wrote " + exeOut);
            if (!exeOnly)
            {
                string dbOut = Path.Combine(gameDir, "AuroraCN.db");
                DatabasePatcher.Write(Path.Combine(gameDir, "AuroraDB.db"), dbOut, tables);
                Console.WriteLine("wrote " + dbOut);
            }
            return Finish(originalExe, exeOut);
        }

        private static int Finish(string originalExe, string exeOut)
        {
            int audit = StringAudit.Check(originalExe, exeOut);
            int cuts = CutAudit.Check(originalExe, exeOut);
            int pairs = PairAudit.Check(originalExe, exeOut);
            return audit != 0 || cuts != 0 || pairs != 0 ? 1 : 0;
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
