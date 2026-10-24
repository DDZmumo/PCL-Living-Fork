using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

// This executable intentionally retains the old assembly/product identity.
// Published old clients validate it, then it hands the existing update protocol
// to the embedded, normally branded application in a separate process.
internal static class Program
{
    private const string AppName = "PCL Living Fork";
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string self = Assembly.GetExecutingAssembly().Location;
            bool legacy = args.Length != 0;
            int parentId;
            string targetName, root;
            if (legacy)
            {
                if (args.Length != 4 || args[0] != "--livingpcl-update" ||
                    !int.TryParse(args[1], out parentId) || parentId <= 0)
                    throw new InvalidOperationException("更新参数无效。");
                if (!string.Equals(Hash(self), args[3], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("兼容更新程序校验失败。");
                var updates = new DirectoryInfo(Path.GetDirectoryName(self));
                if (!updates.Name.Equals("Updates", StringComparison.OrdinalIgnoreCase) || updates.Parent == null ||
                    !updates.Parent.Name.Equals("PCL", StringComparison.OrdinalIgnoreCase) || updates.Parent.Parent == null)
                    throw new InvalidOperationException("兼容更新程序的位置无效。");
                root = updates.Parent.Parent.FullName;
                targetName = args[2];
            }
            else
            {
                root = Path.GetDirectoryName(self);
                targetName = Path.GetFileName(self);
                parentId = Process.GetCurrentProcess().Id;
            }
            if (targetName != Path.GetFileName(targetName) || targetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !targetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新目标文件名无效。");
            string target = Path.Combine(root, targetName);
            try
            {
                using (var parent = Process.GetProcessById(parentId))
                    if (!string.Equals(parent.MainModule.FileName, target, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("更新进程与目标文件不一致。");
            }
            catch (ArgumentException) { }

            string folder = Path.Combine(root, "PCL", "Updates");
            Directory.CreateDirectory(folder);
            string payload = Path.Combine(folder, "pcl-fork-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCLLivingFork.Payload"))
                using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (input == null) throw new InvalidOperationException("更新附件中没有主程序。");
                    input.CopyTo(output);
                    output.Flush(true);
                }
                if (!string.Equals(Hash(payload), PayloadInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("主程序完整性校验失败。");
                var start = new ProcessStartInfo(payload,
                    "--livingpcl-update " + parentId + " \"" + targetName + "\" " + PayloadInfo.Sha256)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = folder
                };
                using (var helper = Process.Start(start))
                {
                    if (helper == null) throw new InvalidOperationException("无法启动新版更新程序。");
                    // Old clients require the staged helper to remain alive for 500 ms.
                    Thread.Sleep(1000);
                    if (!legacy) return 0; // Allow the payload helper to replace this manual download.
                    if (!helper.WaitForExit(60000)) throw new InvalidOperationException("等待新版更新程序超时。");
                    if (helper.ExitCode != 0 || !File.Exists(target) ||
                        !string.Equals(Hash(target), PayloadInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("新版未完成替换，原有程序已保留。");
                }
            }
            catch
            {
                // Never delete/overwrite the installed launcher here; the application's
                // existing validated updater performs replacement and rollback.
                throw;
            }
            return 0;
        }
        catch (Exception error)
        {
            if (Environment.GetEnvironmentVariable("PCL_UPDATE_BRIDGE_NO_UI") != "1")
                MessageBox.Show(error.Message + "\r\n请从 https://github.com/DDZmumo/PCL-Living-Fork/releases/latest 获取最新版。",
                    AppName + " 更新兼容", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    private static string Hash(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
