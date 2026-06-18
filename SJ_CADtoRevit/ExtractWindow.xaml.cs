using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SJ_CADtoRevit
{
    public class BoundaryItem : INotifyPropertyChanged
    {
        public int Index { get; set; }
        public ObjectId Id { get; set; }
        public string Description { get; set; } = "";
        
        private string _fileName = "";
        public string FileName
        {
            get => _fileName;
            set
            {
                _fileName = value;
                OnPropertyChanged(nameof(FileName));
            }
        }

        public Extents3d Extents { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public partial class ExtractWindow : Window
    {
        private ObservableCollection<BoundaryItem> _boundaryItems = new ObservableCollection<BoundaryItem>();
        private ObjectId _referenceBoundaryId = ObjectId.Null;

        public ExtractWindow()
        {
            InitializeComponent();
            lstBoundaries.ItemsSource = _boundaryItems;

            // Subscribe to logger events
            DrawingTrimmer.OnLog += DrawingTrimmer_OnLog;
        }

        protected override void OnClosed(EventArgs e)
        {
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

        private void btnAutoDetect_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            this.Hide();

            try
            {
                Log("도면 전체에서 최적의 도곽을 자동으로 검색 스캔하고 있습니다...");
                
                // Call auto detection
                List<ObjectId> detected = DrawingTrimmer.AutoDetectBoundaries();

                if (detected.Count > 0)
                {
                    _referenceBoundaryId = detected[0]; // Set first as reference
                    PopulateList(detected, doc.Database);
                    
                    // Highlight detected boundaries in AutoCAD Editor
                    ed.SetImpliedSelection(detected.ToArray());
                    
                    lblStatus.Text = $"자동 인식 완료 ({detected.Count}개 도곽)";
                    lblStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                    btnExtract.IsEnabled = true;

                    Log($"[성공] 자동으로 {detected.Count}개의 도곽을 탐색하고 화면상에 하이라이트했습니다.");
                    Log("각 도곽의 저장 파일명을 확인/수정하고 [도면 추출 실행]을 클릭하세요.");
                }
                else
                {
                    lblStatus.Text = "자동 인식 실패";
                    lblStatus.Foreground = System.Windows.Media.Brushes.Red;
                    btnExtract.IsEnabled = false;
                    Log("[경고] 도면 전체에서 유효한 도곽(그룹 크기 1000mm 이상)을 자동 스캔하지 못했습니다.");
                    Log("수동 도곽 지정 버튼을 사용하여 직접 기준 도곽을 지정해 보십시오.");
                }
            }
            catch (System.Exception ex)
            {
                Log($"[오류] 자동 스캔 중 예외 발생: {ex.Message}");
            }
            finally
            {
                this.Show();
            }
        }

        private void btnManualDetect_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            this.Hide();

            try
            {
                Log("수동으로 도곽들을 다중 지정해 주십시오.");
                
                // 다중 선택 필터 설정: BlockReference와 Curve만 허용
                SelectionFilter filter = new SelectionFilter(new TypedValue[]
                {
                    new TypedValue((int)DxfCode.Operator, "<or"),
                    new TypedValue((int)DxfCode.Start, "INSERT"),
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                    new TypedValue((int)DxfCode.Start, "POLYLINE"),
                    new TypedValue((int)DxfCode.Start, "LINE"),
                    new TypedValue((int)DxfCode.Start, "ARC"),
                    new TypedValue((int)DxfCode.Start, "CIRCLE"),
                    new TypedValue((int)DxfCode.Start, "ELLIPSE"),
                    new TypedValue((int)DxfCode.Start, "SPLINE"),
                    new TypedValue((int)DxfCode.Operator, "or>")
                });

                PromptSelectionOptions selOpts = new PromptSelectionOptions
                {
                    MessageForAdding = "\n도곽으로 사용할 기준 객체(블록 또는 폴리선 등)들을 선택하고 엔터(스페이스바)를 누르세요: ",
                    AllowDuplicates = false
                };

                PromptSelectionResult selRes = ed.GetSelection(selOpts, filter);
                if (selRes.Status != PromptStatus.OK || selRes.Value == null)
                {
                    Log("[작업 취소] 기준 도곽 지정이 취소되었습니다.");
                    return;
                }

                Log("선택된 기준 도곽들을 기반으로 도면 내 동일 유형 도곽들을 탐색하는 중...");
                
                HashSet<ObjectId> allDetected = new HashSet<ObjectId>();
                
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject selObj in selRes.Value)
                    {
                        ObjectId refId = selObj.ObjectId;
                        // 각 선택한 기준 도곽에 대해 동일 유형들을 탐색하여 합집합으로 모음
                        List<ObjectId> detected = DrawingTrimmer.DetectBoundaries(refId);
                        foreach (ObjectId id in detected)
                        {
                            allDetected.Add(id);
                        }
                    }
                    tr.Commit();
                }

                List<ObjectId> finalDetectedList = new List<ObjectId>(allDetected);

                if (finalDetectedList.Count > 0)
                {
                    _referenceBoundaryId = finalDetectedList[0]; // 대표 호환용
                    PopulateList(finalDetectedList, doc.Database);
                    
                    // Highlight detected boundaries in AutoCAD Editor
                    ed.SetImpliedSelection(finalDetectedList.ToArray());
                    
                    lblStatus.Text = $"수동 인식 완료 ({finalDetectedList.Count}개 도곽)";
                    lblStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                    btnExtract.IsEnabled = true;

                    Log($"[성공] 지정된 유형들과 매칭되는 총 {finalDetectedList.Count}개의 도곽을 탐색 및 하이라이트했습니다.");
                }
                else
                {
                    lblStatus.Text = "수동 인식 실패";
                    lblStatus.Foreground = System.Windows.Media.Brushes.Red;
                    btnExtract.IsEnabled = false;
                    Log("[경고] 지정된 도곽들과 매칭되는 대상을 검색하지 못했습니다.");
                }
            }
            catch (System.Exception ex)
            {
                Log($"[오류] 수동 탐색 중 예외 발생: {ex.Message}");
            }
            finally
            {
                this.Show();
            }
        }

        private void PopulateList(List<ObjectId> boundaryIds, Database db)
        {
            _boundaryItems.Clear();
            string docName = System.IO.Path.GetFileNameWithoutExtension(db.Filename);
            if (string.IsNullOrEmpty(docName) || docName.StartsWith("Drawing"))
            {
                docName = "FloorPlan";
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < boundaryIds.Count; i++)
                {
                    ObjectId bId = boundaryIds[i];
                    Entity ent = (Entity)tr.GetObject(bId, OpenMode.ForRead);
                    
                    string infoDesc = "";
                    Extents3d extents = new Extents3d();
                    try
                    {
                        extents = ent.GeometricExtents;
                        double w = extents.MaxPoint.X - extents.MinPoint.X;
                        double h = extents.MaxPoint.Y - extents.MinPoint.Y;
                        
                        if (ent is BlockReference br)
                        {
                            string blockName = br.Name;
                            try
                            {
                                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                                blockName = btr.Name;
                            }
                            catch { }
                            infoDesc = $"블록: {blockName} ({w:F0}x{h:F0})";
                        }
                        else
                        {
                            infoDesc = $"폴리선: {ent.Layer} ({w:F0}x{h:F0})";
                        }
                    }
                    catch
                    {
                        infoDesc = ent.GetType().Name + " (크기 확인 불가)";
                    }

                    // Default filename schema: docName_01, docName_02, etc.
                    string defaultFileName = $"{docName}_{i + 1:D2}";

                    _boundaryItems.Add(new BoundaryItem
                    {
                        Index = i + 1,
                        Id = bId,
                        Description = infoDesc,
                        FileName = defaultFileName,
                        Extents = extents
                    });
                }
                tr.Commit();
            }
        }

        private void lstBoundaries_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (lstBoundaries.SelectedItem is BoundaryItem selectedItem)
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Editor ed = doc.Editor;

                // Zoom to the selected boundary
                using (doc.LockDocument())
                {
                    ZoomToExtents(ed, selectedItem.Extents);
                }

                // Highlight this specific boundary
                try
                {
                    ed.SetImpliedSelection(new ObjectId[] { selectedItem.Id });
                }
                catch { }
            }
        }

        private void ZoomToExtents(Editor ed, Extents3d ext)
        {
            try
            {
                using (ViewTableRecord view = ed.GetCurrentView())
                {
                    double width = ext.MaxPoint.X - ext.MinPoint.X;
                    double height = ext.MaxPoint.Y - ext.MinPoint.Y;
                    Point2d center = new Point2d(ext.MinPoint.X + width / 2.0, ext.MinPoint.Y + height / 2.0);

                    view.CenterPoint = center;
                    view.Height = height * 1.25; // 25% margin for context
                    view.Width = width * 1.25;

                    ed.SetCurrentView(view);
                }
                ed.UpdateScreen();
            }
            catch (System.Exception ex)
            {
                Log($"[경고] 캐드 뷰 이동 실패: {ex.Message}");
            }
        }

        private void btnDeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is BoundaryItem item)
            {
                RemoveBoundaryItem(item);
            }
        }

        private void lstBoundaries_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && lstBoundaries.SelectedItem is BoundaryItem item)
            {
                RemoveBoundaryItem(item);
            }
        }

        private void RemoveBoundaryItem(BoundaryItem item)
        {
            _boundaryItems.Remove(item);

            Document doc = Application.DocumentManager.MdiActiveDocument;
            string docName = "FloorPlan";
            if (doc != null)
            {
                docName = System.IO.Path.GetFileNameWithoutExtension(doc.Database.Filename);
                if (string.IsNullOrEmpty(docName) || docName.StartsWith("Drawing"))
                {
                    docName = "FloorPlan";
                }
            }

            for (int i = 0; i < _boundaryItems.Count; i++)
            {
                var current = _boundaryItems[i];
                current.Index = i + 1;

                if (current.FileName.StartsWith(docName + "_"))
                {
                    current.FileName = $"{docName}_{i + 1:D2}";
                }
            }

            if (_boundaryItems.Count > 0)
            {
                lblStatus.Text = $"인식 완료 ({_boundaryItems.Count}개 도곽)";
                lblStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                btnExtract.IsEnabled = true;

                if (doc != null)
                {
                    List<ObjectId> remainingIds = new List<ObjectId>();
                    foreach (var bi in _boundaryItems)
                    {
                        remainingIds.Add(bi.Id);
                    }
                    try
                    {
                        doc.Editor.SetImpliedSelection(remainingIds.ToArray());
                    }
                    catch { }
                }
            }
            else
            {
                lblStatus.Text = "대기 중 (도곽 없음)";
                lblStatus.Foreground = System.Windows.Media.Brushes.Orange;
                btnExtract.IsEnabled = false;

                if (doc != null)
                {
                    try
                    {
                        doc.Editor.SetImpliedSelection(new ObjectId[0]);
                    }
                    catch { }
                }
            }

            Log($"[정보] 항목이 제거되었습니다. 남은 도곽 수: {_boundaryItems.Count}개");
        }

        private void btnExtract_Click(object sender, RoutedEventArgs e)
        {
            if (_boundaryItems.Count == 0)
            {
                MessageBox.Show("인식된 도곽이 없습니다. 먼저 도곽을 인식시켜 주십시오.", "다중도곽추출", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            // 1. Open Output Folder Dialog using Modern OpenFolderDialog
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "추출된 DWG 도면 파일들을 저장할 폴더를 선택하세요",
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                Log("[작업 취소] 저장 폴더 선택이 취소되었습니다.");
                return;
            }

            string folderPath = dialog.FolderName;

            // 2. Collect boundary IDs and individual customized filenames
            List<ObjectId> bIds = new List<ObjectId>();
            List<string> fileNames = new List<string>();

            foreach (var item in _boundaryItems)
            {
                bIds.Add(item.Id);
                
                string name = item.FileName.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    name = $"FloorPlan_{item.Index:D2}";
                }
                fileNames.Add(name);
            }

            // 3. Hide UI
            this.Hide();

            try
            {
                txtLog.Text = $"[작업 중] 개별 수정된 파일명으로 순차 추출 저장하고 있습니다...\n대상 폴더: {folderPath}";

                using (doc.LockDocument())
                {
                    // Execute extraction process
                    DrawingTrimmer.ExtractDrawingsByBoundariesCustomNames(bIds, fileNames, folderPath);
                }
            }
            catch (System.Exception ex)
            {
                Log($"[오류] 추출 중 예외가 발생했습니다: {ex.Message}");
            }
            finally
            {
                // Clear AutoCAD highlight selection
                try
                {
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                }
                catch { }

                this.Show();
            }
        }
    }
}
