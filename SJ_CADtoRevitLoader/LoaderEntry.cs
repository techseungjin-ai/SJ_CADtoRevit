using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using System.Windows.Input;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(SJ_CADtoRevitLoader.LoaderEntry))]

namespace SJ_CADtoRevitLoader
{
    public class LoaderEntry : IExtensionApplication
    {
        // Path to the target plugin assembly to load dynamically
        private static readonly string DllPath = @"C:\Users\최승진\Desktop\안티그래비티\AutoCAD_\SJ_CADtoRevit\SJ_CADtoRevit.bundle\Contents\SJ_CADtoRevit.dll";

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
        /// Creates a ribbon tab inside AutoCAD which calls our load command.
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
                    Title = "Revit 전환설계 (핫리로드)"
                };

                // Create Ribbon Panel
                RibbonPanel panel = new RibbonPanel
                {
                    Source = panelSource
                };
                tab.Panels.Add(panel);

                // Create Ribbon Button
                RibbonButton button = new RibbonButton
                {
                    Text = "Crop & Clean",
                    ShowText = true,
                    Size = RibbonItemSize.Large,
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    CommandParameter = "SJ_LOAD ", // Triggers the memory load command
                    CommandHandler = new RibbonCommandHandler()
                };

                // Add tool tip to button
                RibbonToolTip toolTip = new RibbonToolTip
                {
                    Title = "Crop & Clean (핫리로드)",
                    Content = "클릭 시 메모리 상에서 최신 빌드된 DLL을 로드하여 대화상자를 표시합니다. 디바이스 파일 락이 걸리지 않아 AutoCAD 실행 중에도 자유로운 빌드가 가능합니다."
                };
                button.ToolTip = toolTip;

                panelSource.Items.Add(button);

                // Set tab active
                tab.IsActive = true;
            }
            catch (System.Exception ex)
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[SJ_Loader] 리본 생성 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// Command to load the core assembly into memory and execute the dialog.
        /// Registering both SJ_LOAD and SJ_CROP_TRIM so user can type either.
        /// </summary>
        [CommandMethod("SJ_LOAD")]
        [CommandMethod("SJ_CROP_TRIM")]
        public void LoadAndRun()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            try
            {
                if (!File.Exists(DllPath))
                {
                    doc.Editor.WriteMessage($"\n[SJ_Loader] 오류: 대상 DLL을 찾을 수 없습니다. 경로: {DllPath}");
                    doc.Editor.WriteMessage("\n먼저 SJ_CADtoRevit 프로젝트를 빌드해 주십시오.");
                    return;
                }

                // Read DLL as byte array to bypass file locking on disk
                byte[] buffer = File.ReadAllBytes(DllPath);
                Assembly assembly = Assembly.Load(buffer);

                // Instantiate SJ_CADtoRevit.MainWindow and show it modally
                Type? mainWinType = assembly.GetType("SJ_CADtoRevit.MainWindow");
                if (mainWinType != null)
                {
                    object? windowInstance = Activator.CreateInstance(mainWinType);
                    if (windowInstance is System.Windows.Window win)
                    {
                        Application.ShowModalWindow(win);
                    }
                    else
                    {
                        doc.Editor.WriteMessage("\n[SJ_Loader] 오류: 생성된 인스턴스가 System.Windows.Window 형식이 아닙니다.");
                    }
                }
                else
                {
                    doc.Editor.WriteMessage("\n[SJ_Loader] 오류: SJ_CADtoRevit.MainWindow 타입을 찾을 수 없습니다.");
                }
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\n[SJ_Loader] 로드 중 오류 발생: {ex.Message}");
                if (ex.InnerException != null)
                {
                    doc.Editor.WriteMessage($"\n[SJ_Loader] 상세 오류: {ex.InnerException.Message}");
                }
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
