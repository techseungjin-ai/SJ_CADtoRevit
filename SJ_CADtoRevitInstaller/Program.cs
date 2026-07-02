using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace SJ_CADtoRevitInstaller
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("========================================================");
            Console.WriteLine(" AutoCAD용 SJ_CADtoRevit 플러그인 설치를 시작합니다... ");
            Console.WriteLine("========================================================");

            try
            {
                // Define target paths
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string userPluginsFolder = Path.Combine(appData, "Autodesk", "ApplicationPlugins");
                
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string machinePluginsFolder = Path.Combine(programData, "Autodesk", "ApplicationPlugins");

                // Get embedded resource stream
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = "SJ_CADtoRevitInstaller.bundle.zip";

                using (Stream? resourceStream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (resourceStream == null)
                    {
                        throw new FileNotFoundException("임베디드 리소스를 찾을 수 없습니다: " + resourceName);
                    }

                    // Create temp file for zip
                    string tempZipPath = Path.Combine(Path.GetTempPath(), "SJ_CADtoRevit_bundle.zip");
                    if (File.Exists(tempZipPath))
                    {
                        File.Delete(tempZipPath);
                    }

                    using (FileStream tempFileStream = File.Create(tempZipPath))
                    {
                        resourceStream.CopyTo(tempFileStream);
                    }

                    // Install to User AppData plugins folder (Always succeeds without admin rights)
                    InstallPlugin(tempZipPath, userPluginsFolder);

                    // Install to Machine ProgramData plugins folder (Succeeds if running as Admin)
                    try
                    {
                        InstallPlugin(tempZipPath, machinePluginsFolder);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[안내] ProgramData 폴더 설치 건너뜀 (권한 필요): {ex.Message}");
                    }

                    // Clean up temp file
                    if (File.Exists(tempZipPath))
                    {
                        File.Delete(tempZipPath);
                    }
                }

                Console.WriteLine("\n--------------------------------------------------------");
                Console.WriteLine("설치 성공! SJ_CADtoRevit 플러그인이 성공적으로 등록되었습니다.");
                Console.WriteLine("AutoCAD를 시작하면 자동으로 메뉴 상단에 [SJ_CADtoRevit] 탭이 나타납니다.");
                Console.WriteLine("--------------------------------------------------------");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[오류] 설치 중 문제가 발생했습니다: {ex.Message}");
                Console.ResetColor();
            }

            if (!Console.IsInputRedirected)
            {
                Console.WriteLine("\n계속하려면 아무 키나 누르십시오...");
                Console.ReadKey();
            }
        }

        private static void InstallPlugin(string zipPath, string targetParentFolder)
        {
            if (!Directory.Exists(targetParentFolder))
            {
                Directory.CreateDirectory(targetParentFolder);
            }

            string targetBundleFolder = Path.Combine(targetParentFolder, "SJ_CADtoRevit.bundle");
            if (Directory.Exists(targetBundleFolder))
            {
                Directory.Delete(targetBundleFolder, true);
            }

            ZipFile.ExtractToDirectory(zipPath, targetParentFolder);
            Console.WriteLine($"설치 완료: {targetBundleFolder}");
        }
    }
}
