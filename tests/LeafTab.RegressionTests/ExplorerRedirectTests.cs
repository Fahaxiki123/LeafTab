using System;
using LeafTab.Services;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        internal static void RunExplorerRedirectTests()
        {
            Test("Explorer redirect preserves Unicode, comma and multiple selected names", () =>
            {
                var snapshot = ExplorerRedirectSnapshot.TryCreate(@"D:\文件, 文档\", new[]
                {
                    @"D:\文件, 文档\第二个.txt",
                    @"D:\文件, 文档\第一个, 文件.txt"
                });
                Check(snapshot != null && snapshot.FolderPath == @"D:\文件, 文档");
                Check(snapshot!.SelectedItemNames.Count == 2);
                Check(snapshot.SelectedItemNames[0] == "第一个, 文件.txt" || snapshot.SelectedItemNames[1] == "第一个, 文件.txt");
            });
            Test("Explorer redirect never adopts namespace or mismatched selected children", () =>
            {
                Check(ExplorerRedirectSnapshot.TryCreate("::{26EE0668-A00A-44D7-9371-BEB064C98683}", Array.Empty<string>()) == null);
                Check(ExplorerRedirectSnapshot.TryCreate(@"C:\folder", new[] { @"C:\other\report.txt" }) == null);
                Check(ExplorerRedirectSnapshot.TryCreate(@"C:\folder", new[] { @"C:\folder\..\other\report.txt" }) == null);
                Check(ExplorerRedirectSnapshot.TryCreate(@"C:\folder", new[] { "shell:ControlPanelFolder" }) == null);
                Check(ExplorerRedirectSnapshot.TryCreate(@"\\.\pipe\test", Array.Empty<string>()) == null);
            });
            Test("Explorer redirect retains drive root and UNC selection paths", () =>
            {
                var drive = ExplorerRedirectSnapshot.TryCreate(@"D:\", new[] { @"D:\target.txt" });
                var unc = ExplorerRedirectSnapshot.TryCreate(@"\\server\share\folder\", new[] { @"\\server\share\folder\target.txt" });
                Check(drive != null && drive.FolderPath == @"D:\" && drive.SelectedItemNames[0] == "target.txt");
                Check(unc != null && unc.FolderPath == @"\\server\share\folder" && unc.SelectedItemNames[0] == "target.txt");
            });
            Test("Explorer redirect close guard detects navigation or selection changes", () =>
            {
                var original = ExplorerRedirectSnapshot.TryCreate(@"C:\folder", new[] { @"C:\folder\a.txt", @"C:\folder\b.txt" })!;
                var reordered = ExplorerRedirectSnapshot.TryCreate(@"c:\FOLDER\", new[] { @"c:\FOLDER\B.txt", @"c:\FOLDER\a.txt" })!;
                var changed = ExplorerRedirectSnapshot.TryCreate(@"C:\folder", new[] { @"C:\folder\a.txt" })!;
                var navigated = ExplorerRedirectSnapshot.TryCreate(@"C:\other", new[] { @"C:\other\a.txt", @"C:\other\b.txt" })!;
                Check(original.SameLocationAndSelection(reordered));
                Check(!original.SameLocationAndSelection(changed));
                Check(!original.SameLocationAndSelection(navigated));
            });
        }
    }
}
