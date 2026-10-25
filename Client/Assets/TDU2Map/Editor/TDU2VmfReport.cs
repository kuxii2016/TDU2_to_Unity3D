using System.IO;
using System.Text;
using TDU2.Formats;
using TDU2.IO;
using UnityEditor;
using UnityEngine;

namespace TDU2.Map.EditorTools
{
    /// <summary>
    /// "TDU2/Flash (VMF) analysieren": liest alle .vmf aus Euro/Bnk/frontend/hires (hud, gauges, dashbrd ...)
    /// und schreibt einen Bericht (Bitmaps/Texturquellen, Fonts, Exporte, Labels, Textfeld-Variablen, FSCommands)
    /// nach &lt;Projekt&gt;/VMF_Report.txt. Grundlage für den HUD-Nachbau (siehe Docs/TDU2_VMF_Flash_Format.md).
    /// </summary>
    public static class TDU2VmfReport
    {
        [MenuItem("TDU2/Flash (VMF) analysieren")]
        public static void Run()
        {
            string root = TDU2MapStreamer.ResolveGameRoot("");
            if (root == null) { EditorUtility.DisplayDialog("TDU2", "Spielordner (Euro) nicht gefunden.", "OK"); return; }
            string dir = Path.Combine(root, "Euro", "Bnk", "frontend", "hires");
            if (!Directory.Exists(dir)) dir = Path.Combine(root, "Euro", "Bnk", "frontend");
            var sb = new StringBuilder();
            int files = 0;
            foreach (var bnk in Directory.GetFiles(dir, "*.bnk", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var bank = BnkArchive.Open(bnk);
                    foreach (var e in bank.All(".vmf"))
                    {
                        var vmf = VmfFile.Parse(bank.Read(e));
                        sb.AppendLine(vmf.Report(Path.GetFileName(bnk) + " / " + e.Name));
                        files++;
                    }
                }
                catch (System.Exception ex) { sb.AppendLine($"== {Path.GetFileName(bnk)}: {ex.Message}"); }
            }
            string outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "VMF_Report.txt"));
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[TDU2] {files} VMF-Dateien analysiert -> {outPath}");
            EditorUtility.RevealInFinder(outPath);
        }
    }
}
