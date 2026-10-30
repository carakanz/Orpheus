using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Правила сборок ядра (docs/07, 1.3): netstandard2.1, без Unity, System.Text.Json и генерации кода.
    /// </summary>
    public class CoreAssemblyRulesTests
    {
        private static readonly string[] CoreAssemblies =
        {
            "Orpheus.Core", "Orpheus.WorldGen", "Orpheus.Net", "Orpheus.Save",
        };

        private static readonly string[] ForbiddenAssemblyPrefixes =
        {
            "UnityEngine", "UnityEditor", "Unity.", "System.Text.Json", "System.Reflection.Emit", "Newtonsoft.Json",
        };

        // Ядро ходит через фасад netstandard, поэтому запрет проверяется и по исходникам.
        private static readonly string[] ForbiddenSourceText =
        {
            "UnityEngine", "UnityEditor", "System.Text.Json", "System.Reflection",
        };

        [Fact]
        public void D00_Core_TargetsNetStandard21_WithoutForbiddenReferences()
        {
            foreach (string name in CoreAssemblies)
            {
                Assembly asm = Assembly.Load(new AssemblyName(name));
                var tfm = asm.GetCustomAttribute<TargetFrameworkAttribute>();
                Assert.Equal(".NETStandard,Version=v2.1", tfm?.FrameworkName);

                foreach (AssemblyName reference in asm.GetReferencedAssemblies())
                {
                    string refName = reference.Name ?? string.Empty;
                    foreach (string prefix in ForbiddenAssemblyPrefixes)
                    {
                        Assert.False(refName.StartsWith(prefix, StringComparison.Ordinal),
                            $"{name} ссылается на {refName}");
                    }
                }
            }
        }

        [Fact]
        public void D00_Core_SourcesHaveNoForbiddenNamespaces()
        {
            string root = FindRepoRoot();
            foreach (string name in CoreAssemblies)
            {
                string dir = Path.Combine(root, "src", name);
                Assert.True(Directory.Exists(dir), dir);
                foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    if (IsBuildOutput(file))
                    {
                        continue;
                    }

                    string text = File.ReadAllText(file);
                    foreach (string forbidden in ForbiddenSourceText)
                    {
                        Assert.False(text.Contains(forbidden, StringComparison.Ordinal),
                            $"{file}: найдено «{forbidden}»");
                    }
                }
            }
        }

        private static bool IsBuildOutput(string file)
        {
            string sep = Path.DirectorySeparatorChar.ToString();
            return file.Contains(sep + "obj" + sep, StringComparison.Ordinal)
                || file.Contains(sep + "bin" + sep, StringComparison.Ordinal);
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Orpheus.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
