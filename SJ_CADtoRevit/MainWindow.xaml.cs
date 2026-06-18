using System;
using System.Windows;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SJ_CADtoRevit
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            
            // Subscribe to logger events
            DrawingTrimmer.OnLog += DrawingTrimmer_OnLog;
        }

        protected override void OnClosed(EventArgs e)
        {
            // Unsubscribe to prevent memory leaks
            DrawingTrimmer.OnLog -= DrawingTrimmer_OnLog;
            base.OnClosed(e);
        }

        private void Log(string message)
        {
            txtLog.AppendText("\n" + message);
            txtLog.ScrollToEnd();
        }

        private void DrawingTrimmer_OnLog(string message)
        {
            // Update terminal text safely
            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtLog.AppendText("\n" + message);
                txtLog.ScrollToEnd();
            }));
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void btnRunSingle_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("활성화된 도면을 찾을 수 없습니다.", "SJ_CADtoRevit", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Editor ed = doc.Editor;

            // 1. Hide window to return control to AutoCAD editor
            this.Hide();

            try
            {
                // 2. Select block reference
                PromptEntityOptions blockOpt = new PromptEntityOptions("\n자르거나 가공할 블록(또는 XREF)을 선택하세요: ");
                blockOpt.SetRejectMessage("\n블록 객체가 아닙니다. 다시 선택해주세요.");
                blockOpt.AddAllowedClass(typeof(BlockReference), false);

                PromptEntityResult blockRes = ed.GetEntity(blockOpt);
                if (blockRes.Status != PromptStatus.OK)
                {
                    Log("[작업 취소] 블록 선택이 취소되었습니다.");
                    return;
                }

                // 3. Select boundary curve
                PromptEntityOptions bndOpt = new PromptEntityOptions("\n자르기 경계선(닫힌 폴리선, 원 등)을 선택하세요: ");
                bndOpt.SetRejectMessage("\n곡선 객체가 아닙니다. 다시 선택해주세요.");
                bndOpt.AddAllowedClass(typeof(Curve), false);

                PromptEntityResult bndRes = ed.GetEntity(bndOpt);
                if (bndRes.Status != PromptStatus.OK)
                {
                    Log("[작업 취소] 경계선 선택이 취소되었습니다.");
                    return;
                }

                // Update state and clear log
                txtLog.Text = "[작업 중] 선택한 블록 자르기 작업을 처리하고 있습니다...";

                // Execute single block crop under document lock
                using (doc.LockDocument())
                {
                    DrawingTrimmer.CropSingleBlock(blockRes.ObjectId, bndRes.ObjectId);
                }
            }
            catch (System.Exception ex)
            {
                Log($"[오류] 작업 중 문제가 발생했습니다: {ex.Message}");
            }
            finally
            {
                // 4. Show window back once selection/processing is finished
                this.Show();
            }
        }

        private void btnRunSelection_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("활성화된 도면을 찾을 수 없습니다.", "SJ_CADtoRevit", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Editor ed = doc.Editor;

            // 1. Hide window to return control to AutoCAD editor
            this.Hide();

            try
            {
                // 2. Select Limit Boundary (Outer)
                PromptEntityOptions limitOpt = new PromptEntityOptions("\n자르기 범위 한계 외곽선(닫힌 폴리선, 원 등)을 선택하세요: ");
                limitOpt.SetRejectMessage("\n곡선 객체가 아닙니다. 다시 선택해주세요.");
                limitOpt.AddAllowedClass(typeof(Curve), false);

                PromptEntityResult limitRes = ed.GetEntity(limitOpt);
                if (limitRes.Status != PromptStatus.OK)
                {
                    Log("[작업 취소] 한계 외곽선 선택이 취소되었습니다.");
                    return;
                }

                // 3. Select Crop Boundary (Inner)
                PromptEntityOptions innerOpt = new PromptEntityOptions("\n자르기 기준 외곽선(닫힌 폴리선, 원 등)을 선택하세요: ");
                innerOpt.SetRejectMessage("\n곡선 객체가 아닙니다. 다시 선택해주세요.");
                innerOpt.AddAllowedClass(typeof(Curve), false);

                PromptEntityResult innerRes = ed.GetEntity(innerOpt);
                if (innerRes.Status != PromptStatus.OK)
                {
                    Log("[작업 취소] 자르기 외곽선 선택이 취소되었습니다.");
                    return;
                }

                // Update state and clear log
                txtLog.Text = "[작업 중] 두 외곽선 사이의 객체들을 제거하고 있습니다...";

                // Execute crop between boundaries under document lock
                using (doc.LockDocument())
                {
                    DrawingTrimmer.CropBetweenBoundaries(limitRes.ObjectId, innerRes.ObjectId);
                }
            }
            catch (System.Exception ex)
            {
                Log($"[오류] 작업 중 문제가 발생했습니다: {ex.Message}");
            }
            finally
            {
                // 4. Show window back once selection/processing is finished
                this.Show();
            }
        }

        private void btnRunAll_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("활성화된 도면을 찾을 수 없습니다.", "SJ_CADtoRevit", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Editor ed = doc.Editor;

            // 1. Hide window to return control to AutoCAD editor
            this.Hide();

            try
            {
                // 2. Select boundary curve
                PromptEntityOptions opt = new PromptEntityOptions("\n자르기 경계선(닫힌 폴리선, 원 등)을 선택하세요: ");
                opt.SetRejectMessage("\n곡선 객체가 아닙니다. 다시 선택해주세요.");
                opt.AddAllowedClass(typeof(Curve), false);

                PromptEntityResult res = ed.GetEntity(opt);

                if (res.Status == PromptStatus.OK)
                {
                    // Update state and clear log
                    txtLog.Text = "[작업 중] 도면 정리 및 자르기 작업을 처리하고 있습니다...";

                    // Execute crop and clean under document lock
                    using (doc.LockDocument())
                    {
                        DrawingTrimmer.CropAndClean(res.ObjectId);
                    }
                }
                else
                {
                    Log("[작업 취소] 경계선 선택이 취소되었습니다.");
                }
            }
            catch (System.Exception ex)
            {
                Log($"[오류] 작업 중 문제가 발생했습니다: {ex.Message}");
            }
            finally
            {
                // 3. Show window back once selection/processing is finished
                this.Show();
            }
        }
    }
}
