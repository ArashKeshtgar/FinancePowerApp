using System.Text.RegularExpressions;

namespace FinancePowerApp.Tests;

/// <summary>database/install/erp_install.sql is generated; this fails when a script changed and it wasn't rebuilt.</summary>
public class InstallScriptTests
{
    [Fact]
    public void Install_script_carries_every_script_but_the_demo_seed()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "database");
        var install = File.ReadAllText(Path.Combine(dir, "install", "erp_install.sql")).ReplaceLineEndings("\n");
        var scripts = Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).Where(n => Regex.IsMatch(n!, @"^\d{3}_")).Order().ToList();

        foreach (var name in scripts)
        {
            var listed = install.Contains($"-- ===== {name} =====");
            if (name!.EndsWith("_seed_demo.sql")) { Assert.False(listed, $"{name} must not be in the install script"); continue; }
            Assert.True(listed, $"{name} is missing: run python3 database/make_install_script.py");
            foreach (var batch in Regex.Split(File.ReadAllText(Path.Combine(dir, name)).ReplaceLineEndings("\n"), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch))
                    Assert.True(install.Contains(batch.Trim('\n')), $"{name} changed: run python3 database/make_install_script.py");
        }
    }
}
