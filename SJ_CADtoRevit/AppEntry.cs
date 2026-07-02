using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using System.Windows.Input;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(SJ_CADtoRevit.AppEntry))]

namespace SJ_CADtoRevit
{
    public class AppEntry : IExtensionApplication
    {
        public void Initialize()
        {
            // Subscribe to Idle event to safely create ribbon tab when AutoCAD is fully loaded
            Application.Idle += Application_Idle;
        }

        public void Terminate()
        {
        }

        private void Application_Idle(object? sender, EventArgs e)
        {
            Application.Idle -= Application_Idle;
            CreateRibbonTab();
        }

        /// <summary>
        /// Programmatically creates a Ribbon Tab and Button inside AutoCAD.
        /// </summary>
        private void CreateRibbonTab()
        {
            try
            {
                RibbonControl ribbonControl = ComponentManager.Ribbon;
                if (ribbonControl == null) return;

                string tabId = "SJ_CAD_TO_REVIT_TAB";
                RibbonTab? tab = null;

                // Check if the tab already exists to avoid duplicates
                foreach (var t in ribbonControl.Tabs)
                {
                    if (t.Id == tabId)
                    {
                        tab = t;
                        break;
                    }
                }

                // If not found, create new tab
                if (tab == null)
                {
                    tab = new RibbonTab
                    {
                        Title = "SJ_CADtoRevit",
                        Id = tabId
                    };
                    ribbonControl.Tabs.Add(tab);
                }

                // Create Ribbon Panel Source
                RibbonPanelSource panelSource = new RibbonPanelSource
                {
                    Title = "Revit 전환설계"
                };

                // Create Ribbon Panel
                RibbonPanel panel = new RibbonPanel
                {
                    Source = panelSource
                };
                tab.Panels.Add(panel);

                var cropImage = LoadImageSource("crop.png");
                var extractImage = LoadImageSource("extract.png");

                // Create Ribbon Button 1: Crop & Clean
                RibbonButton btnCrop = new RibbonButton
                {
                    Text = "Crop & Clean",
                    ShowText = true,
                    Size = RibbonItemSize.Large,
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    CommandParameter = "SJ_CROP_TRIM ",
                    CommandHandler = new RibbonCommandHandler(),
                    Image = cropImage,
                    LargeImage = cropImage
                };

                RibbonToolTip toolTipCrop = new RibbonToolTip
                {
                    Title = "Crop & Clean (도면 정리)",
                    Content = "선택한 closed 경계선 외측의 모든 객체(블록 재귀 분해 포함)를 일괄 제거하여 Revit 링크용 도면을 정리합니다."
                };
                btnCrop.ToolTip = toolTipCrop;

                // Create Ribbon Button 2: 다중도곽추출
                RibbonButton btnExtract = new RibbonButton
                {
                    Text = "다중도곽추출",
                    ShowText = true,
                    Size = RibbonItemSize.Large,
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    CommandParameter = "SJ_EXTRACT_DWG ",
                    CommandHandler = new RibbonCommandHandler(),
                    Image = extractImage,
                    LargeImage = extractImage
                };

                RibbonToolTip toolTipExtract = new RibbonToolTip
                {
                    Title = "다중도곽추출 (DWG 분할)",
                    Content = "동일한 형식의 여러 도곽을 도면 전체에서 자동 탐색하여 각각 크롭 처리된 개별 DWG 파일로 일괄 추출합니다."
                };
                btnExtract.ToolTip = toolTipExtract;

                panelSource.Items.Add(btnCrop);
                panelSource.Items.Add(btnExtract);

                // Set tab active
                tab.IsActive = true;
            }
            catch (System.Exception ex)
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[SJ_CADtoRevit] 리본 생성 오류: {ex.Message}");
            }
        }

        private static System.Windows.Media.ImageSource? LoadImageSource(string fileName)
        {
            try
            {
                // Try to find the image in standard Autoloader installation directories first to bypass shadow-copying
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string userPath = System.IO.Path.Combine(appData, "Autodesk", "ApplicationPlugins", "SJ_CADtoRevit.bundle", "Contents", fileName);
                
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string machinePath = System.IO.Path.Combine(programData, "Autodesk", "ApplicationPlugins", "SJ_CADtoRevit.bundle", "Contents", fileName);

                string imagePath = "";
                if (System.IO.File.Exists(userPath))
                {
                    imagePath = userPath;
                }
                else if (System.IO.File.Exists(machinePath))
                {
                    imagePath = machinePath;
                }
                else
                {
                    // Fallback to DLL directory
                    string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    string dllDir = System.IO.Path.GetDirectoryName(dllPath) ?? "";
                    string fallbackPath = System.IO.Path.Combine(dllDir, fileName);
                    if (System.IO.File.Exists(fallbackPath))
                    {
                        imagePath = fallbackPath;
                    }
                }

                if (!string.IsNullOrEmpty(imagePath))
                {
                    System.Windows.Media.Imaging.BitmapImage bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    string uriPath = "file:///" + imagePath.Replace('\\', '/');
                    bmp.UriSource = new Uri(uriPath, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Command to show the WPF Window for Crop & Clean.
        /// </summary>
        [CommandMethod("SJ_CROP_TRIM")]
        public void RunCropTrimDialog()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            try
            {
                MainWindow win = new MainWindow();
                Application.ShowModalWindow(win);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\n[SJ_CADtoRevit] 대화창 표시 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// Command to show the WPF Window for Multi-Plot DWG Extraction.
        /// </summary>
        [CommandMethod("SJ_EXTRACT_DWG")]
        public void RunExtractDwgDialog()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            try
            {
                ExtractWindow win = new ExtractWindow();
                Application.ShowModalWindow(win);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\n[SJ_CADtoRevit] 추출 대화창 표시 오류: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Router command handler for AutoCAD Ribbon clicks.
    /// </summary>
    public class RibbonCommandHandler : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public void Execute(object? parameter)
        {
            if (parameter is RibbonButton button && button.CommandParameter != null)
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.SendStringToExecute(button.CommandParameter.ToString()!, true, false, false);
                }
            }
        }
    }
}
