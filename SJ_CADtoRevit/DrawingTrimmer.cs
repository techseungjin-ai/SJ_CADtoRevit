using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace SJ_CADtoRevit
{
    public static class DrawingTrimmer
    {
        public delegate void LogMessageHandler(string message);
        public static event LogMessageHandler? OnLog;

        private static void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }

        /// <summary>
        /// 3) 기존처럼 외곽선 선택하면 외곽선 외 전체부분 삭제 (전체 도면 자르기)
        /// </summary>
        public static void CropAndClean(ObjectId boundaryId)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            Log("도면 전체 자르기 작업을 시작합니다...");

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 1. Bind XREFs first so they can be exploded
                BindAllXrefs(db, tr);

                // 2. Read and validate the boundary curve
                Curve? boundaryCurve = tr.GetObject(boundaryId, OpenMode.ForRead) as Curve;
                if (boundaryCurve == null || !boundaryCurve.Closed)
                {
                    Log("오류: 선택한 경계선이 닫힌 곡선이 아닙니다.");
                    return;
                }

                Extents3d bndExtents = boundaryCurve.GeometricExtents;
                Log($"경계 범위: 최소={bndExtents.MinPoint}, 최대={bndExtents.MaxPoint}");

                // Tessellate the boundary curve sequentially using parameter-based sampling
                List<Point2d> boundaryVertices = TessellateCurve(boundaryCurve, 300);
                Log($"경계선 다각형 분석 완료 ({boundaryVertices.Count} 포인트 구성)");

                // Open ModelSpace for write
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Lists to hold entities
                List<ObjectId> entitiesToDelete = new List<ObjectId>();
                List<Entity> dbResidentEntities = new List<Entity>();
                List<BlockReference> blocksToProcess = new List<BlockReference>();

                int totalCount = 0;
                int outsideBBoxCount = 0;

                // 3. Scan ModelSpace to filter database-resident entities
                foreach (ObjectId entId in modelSpace)
                {
                    if (entId == boundaryId)
                        continue; // Skip boundary itself

                    Entity ent = (Entity)tr.GetObject(entId, OpenMode.ForRead);
                    totalCount++;

                    try
                    {
                        Extents3d entExtents = ent.GeometricExtents;

                        // Quick filter: if completely outside boundary extents, delete it immediately
                        if (entExtents.MaxPoint.X < bndExtents.MinPoint.X || entExtents.MinPoint.X > bndExtents.MaxPoint.X ||
                            entExtents.MaxPoint.Y < bndExtents.MinPoint.Y || entExtents.MinPoint.Y > bndExtents.MaxPoint.Y)
                        {
                            entitiesToDelete.Add(entId);
                            outsideBBoxCount++;
                            continue;
                        }
                    }
                    catch
                    {
                        // Fallback to detailed check if extents calculation fails
                    }

                    if (ent is BlockReference blockRef)
                    {
                        blocksToProcess.Add(blockRef);
                    }
                    else
                    {
                        dbResidentEntities.Add(ent);
                    }
                }

                Log($"모형 공간 탐색: 전체 {totalCount}개 객체 중 {outsideBBoxCount}개 외측 삭제 확정");

                // 4. Explode crossing blocks recursively, delete outer, keep inner
                int blocksDeleted = 0;
                int blocksExploded = 0;
                int blocksKept = 0;
                List<Entity> explodedList = new List<Entity>();

                foreach (BlockReference blockRef in blocksToProcess)
                {
                    ContainmentResult relation = CheckBlockContainment(blockRef, boundaryCurve, boundaryVertices);
                    
                    if (relation == ContainmentResult.Outside)
                    {
                        entitiesToDelete.Add(blockRef.Id);
                        blocksDeleted++;
                    }
                    else if (relation == ContainmentResult.Inside)
                    {
                        // Keep block intact (database-resident)
                        blocksKept++;
                    }
                    else // Crossing
                    {
                        // Explode recursively into DB (Immediate DB Append)
                        List<Entity> subEntities = ExplodeBlockRecursive(blockRef, boundaryCurve, boundaryVertices, modelSpace, tr);
                        explodedList.AddRange(subEntities);
                        
                        entitiesToDelete.Add(blockRef.Id); // Delete original crossing block reference
                        blocksExploded++;
                    }
                }

                Log($"블록 처리 결과: 내측 유지={blocksKept}개, 외측 삭제={blocksDeleted}개, 경계 분해={blocksExploded}개");

                // 5. Process database-resident non-block entities
                int curvesTrimmed = 0;
                int dbObjectsDeleted = 0;
                int dbObjectsKept = 0;

                foreach (Entity ent in dbResidentEntities)
                {
                    ProcessEntity(ent, boundaryCurve, boundaryVertices, modelSpace, tr, entitiesToDelete, ref curvesTrimmed, ref dbObjectsDeleted, ref dbObjectsKept);
                }

                // 6. Process exploded entities (already in DB)
                int expObjectsDeleted = 0;
                int expObjectsKept = 0;

                foreach (Entity ent in explodedList)
                {
                    ProcessEntity(ent, boundaryCurve, boundaryVertices, modelSpace, tr, entitiesToDelete, ref curvesTrimmed, ref expObjectsDeleted, ref expObjectsKept);
                }

                // 7. Delete marked database-resident entities
                int actualDeleted = 0;
                foreach (ObjectId delId in entitiesToDelete)
                {
                    if (delId.IsValid && !delId.IsErased)
                    {
                        DBObject obj = tr.GetObject(delId, OpenMode.ForWrite);
                        obj.Erase();
                        actualDeleted++;
                    }
                }

                tr.Commit();
                Log($"도면 정리가 완료되었습니다. 지워진 외측 객체 수: {actualDeleted}개");
            }
        }

        /// <summary>
        /// 1) 단일 블록선택 후 외곽선 선택 (외곽선 내부만 남기고 나머지 부분 삭제)
        /// </summary>
        public static void CropSingleBlock(ObjectId blockRefId, ObjectId boundaryId)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            Log("선택한 블록 자르기 작업을 시작합니다...");

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 1. Bind XREFs first so they can be exploded
                BindAllXrefs(db, tr);

                // 2. Read and validate the boundary curve
                Curve? boundaryCurve = tr.GetObject(boundaryId, OpenMode.ForRead) as Curve;
                if (boundaryCurve == null || !boundaryCurve.Closed)
                {
                    Log("오류: 선택한 경계선이 닫힌 곡선이 아닙니다.");
                    return;
                }

                List<Point2d> boundaryVertices = TessellateCurve(boundaryCurve, 300);

                // 3. Open ModelSpace for write
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // 4. Read the selected block reference
                BlockReference? blockRef = tr.GetObject(blockRefId, OpenMode.ForRead) as BlockReference;
                if (blockRef == null)
                {
                    Log("오류: 선택한 객체가 블록이 아닙니다.");
                    return;
                }

                List<ObjectId> entitiesToDelete = new List<ObjectId>();
                ContainmentResult relation = CheckBlockContainment(blockRef, boundaryCurve, boundaryVertices);
                
                if (relation == ContainmentResult.Outside)
                {
                    entitiesToDelete.Add(blockRef.Id);
                    Log("선택한 블록이 경계선 외부에 있으므로 삭제되었습니다.");
                }
                else if (relation == ContainmentResult.Inside)
                {
                    Log("선택한 블록이 경계선 내부에 완전히 포함되어 있어 그대로 유지됩니다.");
                }
                else // Crossing
                {
                    Log("선택한 블록이 경계선과 교차하여 분해 및 트리밍 작업을 진행합니다...");
                    
                    // Explode recursively into DB (Immediate DB Append)
                    List<Entity> subEntities = ExplodeBlockRecursive(blockRef, boundaryCurve, boundaryVertices, modelSpace, tr);
                    
                    int curvesTrimmed = 0;
                    int expObjectsDeleted = 0;
                    int expObjectsKept = 0;

                    foreach (Entity ent in subEntities)
                    {
                        ProcessEntity(ent, boundaryCurve, boundaryVertices, modelSpace, tr, entitiesToDelete, ref curvesTrimmed, ref expObjectsDeleted, ref expObjectsKept);
                    }

                    entitiesToDelete.Add(blockRef.Id); // Delete the original crossing block reference
                    Log($"블록 분해 처리 결과: 유지={expObjectsKept}개, 삭제={expObjectsDeleted}개, 트리밍={curvesTrimmed}개");
                }

                // 5. Delete marked entities
                int actualDeleted = 0;
                foreach (ObjectId delId in entitiesToDelete)
                {
                    if (delId.IsValid && !delId.IsErased)
                    {
                        DBObject obj = tr.GetObject(delId, OpenMode.ForWrite);
                        obj.Erase();
                        actualDeleted++;
                    }
                }

                tr.Commit();
                Log("선택한 블록의 자르기 작업이 완료되었습니다.");
            }
        }

        /// <summary>
        /// 2) 한계 외곽선과 자르기 외곽선 사이의 객체들만 삭제하는 기능
        /// (한계 외곽선 바깥에 있는 객체들과 자르기 외곽선 내부에 있는 객체들은 영향받지 않도록 보존)
        /// </summary>
        public static void CropBetweenBoundaries(ObjectId limitBoundaryId, ObjectId innerBoundaryId)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            Log("선택 영역(한계 외곽선 사이) 자르기 작업을 시작합니다...");

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 1. Bind XREFs first so they can be exploded
                BindAllXrefs(db, tr);

                // 2. Read boundaries
                Curve? limitCurve = tr.GetObject(limitBoundaryId, OpenMode.ForRead) as Curve;
                Curve? innerCurve = tr.GetObject(innerBoundaryId, OpenMode.ForRead) as Curve;

                if (limitCurve == null || !limitCurve.Closed || innerCurve == null || !innerCurve.Closed)
                {
                    Log("오류: 선택한 경계선이 닫힌 곡선이 아닙니다.");
                    return;
                }

                List<Point2d> limitVertices = TessellateCurve(limitCurve, 300);
                List<Point2d> innerVertices = TessellateCurve(innerCurve, 300);

                // Open ModelSpace for write
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                List<ObjectId> entitiesToDelete = new List<ObjectId>();
                List<Entity> dbResidentEntities = new List<Entity>();
                List<BlockReference> blocksToProcess = new List<BlockReference>();

                // 3. Scan ModelSpace to filter targets
                foreach (ObjectId entId in modelSpace)
                {
                    if (entId == limitBoundaryId || entId == innerBoundaryId)
                        continue;

                    Entity ent = (Entity)tr.GetObject(entId, OpenMode.ForRead);

                    // Optimization: if completely outside limitBoundary bounding box, keep it untouched (skip processing)
                    try
                    {
                        Extents3d entExt = ent.GeometricExtents;
                        Extents3d limitExt = limitCurve.GeometricExtents;
                        if (entExt.MaxPoint.X < limitExt.MinPoint.X || entExt.MinPoint.X > limitExt.MaxPoint.X ||
                            entExt.MaxPoint.Y < limitExt.MinPoint.Y || entExt.MinPoint.Y > limitExt.MaxPoint.Y)
                        {
                            continue; // Skip (kept as-is)
                        }
                    }
                    catch { }

                    if (ent is BlockReference blockRef)
                    {
                        blocksToProcess.Add(blockRef);
                    }
                    else
                    {
                        dbResidentEntities.Add(ent);
                    }
                }

                // 4. Process blocks
                List<Entity> explodedList = new List<Entity>();
                foreach (BlockReference blockRef in blocksToProcess)
                {
                    ContainmentResult relLimit = CheckBlockContainment(blockRef, limitCurve, limitVertices);
                    ContainmentResult relInner = CheckBlockContainment(blockRef, innerCurve, innerVertices);

                    if (relLimit == ContainmentResult.Outside || relInner == ContainmentResult.Inside)
                    {
                        // Keep intact (outside limit, or inside inner)
                    }
                    else if (relLimit == ContainmentResult.Inside && relInner == ContainmentResult.Outside)
                    {
                        entitiesToDelete.Add(blockRef.Id); // Completely in delete zone
                    }
                    else // Crossing either boundary
                    {
                        List<Entity> subEntities = ExplodeBlockRecursiveBetween(blockRef, limitCurve, limitVertices, innerCurve, innerVertices, modelSpace, tr);
                        explodedList.AddRange(subEntities);
                        entitiesToDelete.Add(blockRef.Id);
                    }
                }

                // 5. Process db-resident entities
                int curvesTrimmed = 0;
                int dbObjectsDeleted = 0;
                int dbObjectsKept = 0;
                foreach (Entity ent in dbResidentEntities)
                {
                    ProcessEntityBetween(ent, limitCurve, limitVertices, innerCurve, innerVertices, modelSpace, tr, entitiesToDelete, ref curvesTrimmed, ref dbObjectsDeleted, ref dbObjectsKept);
                }

                // 6. Process exploded entities
                int expObjectsDeleted = 0;
                int expObjectsKept = 0;
                foreach (Entity ent in explodedList)
                {
                    ProcessEntityBetween(ent, limitCurve, limitVertices, innerCurve, innerVertices, modelSpace, tr, entitiesToDelete, ref curvesTrimmed, ref expObjectsDeleted, ref expObjectsKept);
                }

                // 7. Delete marked entities
                int actualDeleted = 0;
                foreach (ObjectId delId in entitiesToDelete)
                {
                    if (delId.IsValid && !delId.IsErased)
                    {
                        DBObject obj = tr.GetObject(delId, OpenMode.ForWrite);
                        obj.Erase();
                        actualDeleted++;
                    }
                }

                tr.Commit();
                Log($"선택 영역(한계선 사이) 자르기가 완료되었습니다. 지워진 객체 수: {actualDeleted}개");
            }
        }

        private static void BindAllXrefs(Database db, Transaction tr)
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            ObjectIdCollection xrefIds = new ObjectIdCollection();

            foreach (ObjectId btrId in bt)
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (btr.IsFromExternalReference)
                {
                    xrefIds.Add(btrId);
                }
            }

            if (xrefIds.Count > 0)
            {
                Log($"외부참조(XREF) {xrefIds.Count}개를 도면에 결합(Bind)하는 중...");
                try
                {
                    db.BindXrefs(xrefIds, true);
                    Log("외부참조 결합이 완료되었습니다.");
                }
                catch (System.Exception ex)
                {
                    Log($"경고: 외부참조 결합에 실패했습니다: {ex.Message}");
                }
            }
        }

        private static void ProcessEntity(
            Entity ent, 
            Curve boundaryCurve, 
            List<Point2d> boundaryVertices, 
            BlockTableRecord modelSpace,
            Transaction tr,
            List<ObjectId> entitiesToDelete,
            ref int curvesTrimmed,
            ref int objectsDeleted,
            ref int objectsKept)
        {
            if (ent is Curve curve)
            {
                // Find intersections with boundary curve
                Point3dCollection intersectPoints = new Point3dCollection();
                try
                {
                    curve.IntersectWith(boundaryCurve, Intersect.OnBothOperands, intersectPoints, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }

                if (intersectPoints.Count > 0)
                {
                    // Curve crosses boundary: split it
                    DBObjectCollection? splitCurves = null;
                    try
                    {
                        splitCurves = curve.GetSplitCurves(intersectPoints);
                    }
                    catch { }

                    if (splitCurves != null && splitCurves.Count > 0)
                    {
                        foreach (DBObject obj in splitCurves)
                        {
                            if (obj is Curve splitSegment)
                            {
                                // Check if midpoint of segment is inside the boundary
                                if (CheckMidpointInside(splitSegment, boundaryCurve, boundaryVertices))
                                {
                                    // Copy properties from original curve
                                    splitSegment.Layer = curve.Layer;
                                    splitSegment.Color = curve.Color;
                                    splitSegment.Linetype = curve.Linetype;
                                    splitSegment.LineWeight = curve.LineWeight;

                                    // Add new segment to database
                                    modelSpace.AppendEntity(splitSegment);
                                    tr.AddNewlyCreatedDBObject(splitSegment, true);
                                    objectsKept++;
                                }
                                else
                                {
                                    splitSegment.Dispose();
                                }
                            }
                        }

                        // Clean up original curve (already in DB)
                        entitiesToDelete.Add(curve.Id);
                        curvesTrimmed++;
                    }
                    else
                    {
                        // Split failed: fallback to midpoint check
                        if (CheckMidpointInside(curve, boundaryCurve, boundaryVertices))
                        {
                            objectsKept++;
                        }
                        else
                        {
                            entitiesToDelete.Add(curve.Id);
                            objectsDeleted++;
                        }
                    }
                }
                else
                {
                    // No intersections: completely inside or completely outside
                    if (CheckMidpointInside(curve, boundaryCurve, boundaryVertices))
                    {
                        objectsKept++;
                    }
                    else
                    {
                        entitiesToDelete.Add(curve.Id);
                        objectsDeleted++;
                    }
                }
            }
            else
            {
                // Non-curve entity (Text, Dimension, Hatch, Point, BlockReference, etc.)
                Point3d testPoint = GetEntityRepresentativePoint(ent);
                
                // Hatch 특수 처리: 걸쳐있는 해치는 유지
                if (ent is Hatch hatch)
                {
                    bool shouldKeep = false;
                    try
                    {
                        Extents3d ext = hatch.GeometricExtents;
                        Point3d center = new Point3d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                            (ext.MinPoint.Z + ext.MaxPoint.Z) / 2.0
                        );
                        if (IsPointInsideCurve(boundaryCurve, boundaryVertices, center))
                        {
                            shouldKeep = true;
                        }
                        else
                        {
                            Point3d[] corners = new Point3d[]
                            {
                                ext.MinPoint,
                                new Point3d(ext.MinPoint.X, ext.MaxPoint.Y, ext.MinPoint.Z),
                                ext.MaxPoint,
                                new Point3d(ext.MaxPoint.X, ext.MinPoint.Y, ext.MinPoint.Z)
                            };
                            foreach (Point3d pt in corners)
                            {
                                if (IsPointInsideCurve(boundaryCurve, boundaryVertices, pt))
                                {
                                    shouldKeep = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {
                        shouldKeep = true; // Extents 계산 실패 시 안전하게 유지
                    }

                    if (shouldKeep)
                    {
                        objectsKept++;
                    }
                    else
                    {
                        entitiesToDelete.Add(hatch.Id);
                        objectsDeleted++;
                    }
                }
                else
                {
                    if (IsPointInsideCurve(boundaryCurve, boundaryVertices, testPoint))
                    {
                        objectsKept++;
                    }
                    else
                    {
                        entitiesToDelete.Add(ent.Id);
                        objectsDeleted++;
                    }
                }
            }
        }

        private static void ProcessEntityBetween(
            Entity ent, 
            Curve limitCurve, 
            List<Point2d> limitVertices, 
            Curve innerCurve, 
            List<Point2d> innerVertices,
            BlockTableRecord modelSpace,
            Transaction tr,
            List<ObjectId> entitiesToDelete,
            ref int curvesTrimmed,
            ref int objectsDeleted,
            ref int objectsKept)
        {
            if (ent is Curve curve)
            {
                // Find all intersections with both boundaries
                Point3dCollection intersectPoints = new Point3dCollection();
                try
                {
                    curve.IntersectWith(limitCurve, Intersect.OnBothOperands, intersectPoints, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }
                try
                {
                    curve.IntersectWith(innerCurve, Intersect.OnBothOperands, intersectPoints, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }

                if (intersectPoints.Count > 0)
                {
                    // Split the curve
                    DBObjectCollection? splitCurves = null;
                    try
                    {
                        splitCurves = curve.GetSplitCurves(intersectPoints);
                    }
                    catch { }

                    if (splitCurves != null && splitCurves.Count > 0)
                    {
                        foreach (DBObject obj in splitCurves)
                        {
                            if (obj is Curve splitSegment)
                            {
                                if (CheckShouldKeepBetween(splitSegment, limitCurve, limitVertices, innerCurve, innerVertices))
                                {
                                    splitSegment.Layer = curve.Layer;
                                    splitSegment.Color = curve.Color;
                                    splitSegment.Linetype = curve.Linetype;
                                    splitSegment.LineWeight = curve.LineWeight;

                                    modelSpace.AppendEntity(splitSegment);
                                    tr.AddNewlyCreatedDBObject(splitSegment, true);
                                    objectsKept++;
                                }
                                else
                                {
                                    splitSegment.Dispose();
                                }
                            }
                        }

                        entitiesToDelete.Add(curve.Id);
                        curvesTrimmed++;
                    }
                    else
                    {
                        if (CheckShouldKeepBetween(curve, limitCurve, limitVertices, innerCurve, innerVertices))
                        {
                            objectsKept++;
                        }
                        else
                        {
                            entitiesToDelete.Add(curve.Id);
                            objectsDeleted++;
                        }
                    }
                }
                else
                {
                    if (CheckShouldKeepBetween(curve, limitCurve, limitVertices, innerCurve, innerVertices))
                    {
                        objectsKept++;
                    }
                    else
                    {
                        entitiesToDelete.Add(curve.Id);
                        objectsDeleted++;
                    }
                }
            }
            else
            {
                Point3d testPoint = GetEntityRepresentativePoint(ent);

                // Hatch 특수 처리: 한계선 밖이나 내측선 안에 걸치는 해치들은 보존
                if (ent is Hatch hatch)
                {
                    bool shouldKeep = false;
                    try
                    {
                        Extents3d ext = hatch.GeometricExtents;
                        Point3d center = new Point3d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                            (ext.MinPoint.Z + ext.MaxPoint.Z) / 2.0
                        );
                        bool centerLimit = IsPointInsideCurve(limitCurve, limitVertices, center);
                        bool centerInner = IsPointInsideCurve(innerCurve, innerVertices, center);

                        if (!centerLimit || centerInner)
                        {
                            shouldKeep = true;
                        }
                        else
                        {
                            Point3d[] corners = new Point3d[]
                            {
                                ext.MinPoint,
                                new Point3d(ext.MinPoint.X, ext.MaxPoint.Y, ext.MinPoint.Z),
                                ext.MaxPoint,
                                new Point3d(ext.MaxPoint.X, ext.MinPoint.Y, ext.MinPoint.Z)
                            };
                            foreach (Point3d pt in corners)
                            {
                                bool ptLimit = IsPointInsideCurve(limitCurve, limitVertices, pt);
                                bool ptInner = IsPointInsideCurve(innerCurve, innerVertices, pt);
                                if (!ptLimit || ptInner)
                                {
                                    shouldKeep = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {
                        shouldKeep = true;
                    }

                    if (shouldKeep)
                    {
                        objectsKept++;
                    }
                    else
                    {
                        entitiesToDelete.Add(hatch.Id);
                        objectsDeleted++;
                    }
                }
                else
                {
                    bool insideLimit = IsPointInsideCurve(limitCurve, limitVertices, testPoint);
                    bool insideInner = IsPointInsideCurve(innerCurve, innerVertices, testPoint);

                    if (!insideLimit || insideInner)
                    {
                        objectsKept++;
                    }
                    else
                    {
                        entitiesToDelete.Add(ent.Id);
                        objectsDeleted++;
                    }
                }
            }
        }

        private static bool CheckShouldKeepBetween(Curve curve, Curve limitCurve, List<Point2d> limitVertices, Curve innerCurve, List<Point2d> innerVertices)
        {
            Point3d testPt;
            bool hasPoint = false;

            try
            {
                double midParam = (curve.StartParam + curve.EndParam) / 2.0;
                testPt = curve.GetPointAtParameter(midParam);
                hasPoint = true;
            }
            catch
            {
                try
                {
                    testPt = curve.StartPoint;
                    hasPoint = true;
                }
                catch
                {
                    try
                    {
                        Extents3d ext = curve.GeometricExtents;
                        testPt = new Point3d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                            (ext.MinPoint.Z + ext.MaxPoint.Z) / 2.0
                        );
                        hasPoint = true;
                    }
                    catch
                    {
                        testPt = Point3d.Origin;
                    }
                }
            }

            if (hasPoint)
            {
                bool insideLimit = IsPointInsideCurve(limitCurve, limitVertices, testPt);
                bool insideInner = IsPointInsideCurve(innerCurve, innerVertices, testPt);
                return !insideLimit || insideInner;
            }
            return true;
        }

        private static bool CheckMidpointInside(Curve curve, Curve boundaryCurve, List<Point2d> boundaryVertices)
        {
            Point3d testPt;
            bool hasPoint = false;

            try
            {
                double midParam = (curve.StartParam + curve.EndParam) / 2.0;
                testPt = curve.GetPointAtParameter(midParam);
                hasPoint = true;
            }
            catch
            {
                try
                {
                    testPt = curve.StartPoint;
                    hasPoint = true;
                }
                catch
                {
                    try
                    {
                        Extents3d ext = curve.GeometricExtents;
                        testPt = new Point3d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                            (ext.MinPoint.Z + ext.MaxPoint.Z) / 2.0
                        );
                        hasPoint = true;
                    }
                    catch
                    {
                        testPt = Point3d.Origin;
                    }
                }
            }

            if (hasPoint)
            {
                return IsPointInsideCurve(boundaryCurve, boundaryVertices, testPt);
            }
            return false;
        }

        private static Point3d GetEntityRepresentativePoint(Entity ent)
        {
            if (ent is DBText txt)
                return txt.Position;
            if (ent is MText mtxt)
                return mtxt.Location;
            if (ent is DBPoint pt)
                return pt.Position;
            if (ent is Dimension dim)
                return dim.TextPosition;

            try
            {
                Extents3d ext = ent.GeometricExtents;
                return new Point3d(
                    (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                    (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                    (ext.MinPoint.Z + ext.MaxPoint.Z) / 2.0
                );
            }
            catch
            {
                return Point3d.Origin;
            }
        }

        private static List<Entity> ExplodeBlockRecursive(
            BlockReference blockRef, 
            Curve boundaryCurve, 
            List<Point2d> boundaryVertices, 
            BlockTableRecord modelSpace,
            Transaction tr)
        {
            List<Entity> result = new List<Entity>();
            DBObjectCollection explodedList = new DBObjectCollection();

            try
            {
                BlockTableRecord blockDef = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForWrite);
                blockDef.Explodable = true;

                blockRef.Explode(explodedList);

                foreach (DBObject obj in explodedList)
                {
                    if (obj is BlockReference subRef)
                    {
                        if (!subRef.Visible)
                        {
                            subRef.Dispose();
                            continue;
                        }

                        ContainmentResult relation = CheckBlockContainment(subRef, boundaryCurve, boundaryVertices);
                        if (relation == ContainmentResult.Inside)
                        {
                            PropagateBlockProperties(subRef, blockRef);
                            modelSpace.AppendEntity(subRef);
                            tr.AddNewlyCreatedDBObject(subRef, true);
                            result.Add(subRef);
                        }
                        else if (relation == ContainmentResult.Outside)
                        {
                            subRef.Dispose();
                        }
                        else // Crossing
                        {
                            PropagateBlockProperties(subRef, blockRef);
                            List<Entity> nestedList = ExplodeBlockRecursive(subRef, boundaryCurve, boundaryVertices, modelSpace, tr);
                            result.AddRange(nestedList);
                            subRef.Dispose();
                        }
                    }
                    else if (obj is Entity ent)
                    {
                        if (!ent.Visible)
                        {
                            ent.Dispose();
                            continue;
                        }

                        PropagateBlockProperties(ent, blockRef);
                        modelSpace.AppendEntity(ent);
                        tr.AddNewlyCreatedDBObject(ent, true);
                        result.Add(ent);
                    }
                    else
                    {
                        obj.Dispose();
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log($"경고: 블록 '{blockRef.Name}' 분해 실패: {ex.Message}");
            }

            return result;
        }

        private static List<Entity> ExplodeBlockRecursiveBetween(
            BlockReference blockRef, 
            Curve limitCurve, 
            List<Point2d> limitVertices, 
            Curve innerCurve, 
            List<Point2d> innerVertices,
            BlockTableRecord modelSpace,
            Transaction tr)
        {
            List<Entity> result = new List<Entity>();
            DBObjectCollection explodedList = new DBObjectCollection();

            try
            {
                BlockTableRecord blockDef = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForWrite);
                blockDef.Explodable = true;

                blockRef.Explode(explodedList);

                foreach (DBObject obj in explodedList)
                {
                    if (obj is BlockReference subRef)
                    {
                        if (!subRef.Visible)
                        {
                            subRef.Dispose();
                            continue;
                        }

                        ContainmentResult relLimit = CheckBlockContainment(subRef, limitCurve, limitVertices);
                        ContainmentResult relInner = CheckBlockContainment(subRef, innerCurve, innerVertices);

                        // Keep condition: Outside limit or Inside inner
                        if (relLimit == ContainmentResult.Outside || relInner == ContainmentResult.Inside)
                        {
                            PropagateBlockProperties(subRef, blockRef);
                            modelSpace.AppendEntity(subRef);
                            tr.AddNewlyCreatedDBObject(subRef, true);
                            result.Add(subRef);
                        }
                        // Delete condition: Inside limit and Outside inner
                        else if (relLimit == ContainmentResult.Inside && relInner == ContainmentResult.Outside)
                        {
                            subRef.Dispose();
                        }
                        else // Crossing either boundary
                        {
                            PropagateBlockProperties(subRef, blockRef);
                            List<Entity> nestedList = ExplodeBlockRecursiveBetween(subRef, limitCurve, limitVertices, innerCurve, innerVertices, modelSpace, tr);
                            result.AddRange(nestedList);
                            subRef.Dispose();
                        }
                    }
                    else if (obj is Entity ent)
                    {
                        if (!ent.Visible)
                        {
                            ent.Dispose();
                            continue;
                        }

                        PropagateBlockProperties(ent, blockRef);
                        modelSpace.AppendEntity(ent);
                        tr.AddNewlyCreatedDBObject(ent, true);
                        result.Add(ent);
                    }
                    else
                    {
                        obj.Dispose();
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log($"경고: 블록 '{blockRef.Name}' 분해 실패: {ex.Message}");
            }

            return result;
        }

        private static void PropagateBlockProperties(Entity ent, BlockReference parentBlockRef)
        {
            if (ent.Layer == "0")
            {
                ent.Layer = parentBlockRef.Layer;
            }
            if (ent.Color.IsByBlock)
            {
                ent.Color = parentBlockRef.Color;
            }
            if (ent.Linetype == "ByBlock")
            {
                ent.Linetype = parentBlockRef.Linetype;
            }
            if (ent.LineWeight == LineWeight.ByBlock)
            {
                ent.LineWeight = parentBlockRef.LineWeight;
            }
        }

        private enum ContainmentResult
        {
            Inside,
            Outside,
            Crossing
        }

        private static ContainmentResult CheckBlockContainment(
            BlockReference blockRef, 
            Curve boundaryCurve, 
            List<Point2d> boundaryVertices)
        {
            Extents3d bndExtents = boundaryCurve.GeometricExtents;
            Extents3d blockExtents;

            try
            {
                blockExtents = blockRef.GeometricExtents;
            }
            catch
            {
                bool insertionInside = IsPointInsideCurve(boundaryCurve, boundaryVertices, blockRef.Position);
                return insertionInside ? ContainmentResult.Crossing : ContainmentResult.Outside;
            }

            if (blockExtents.MaxPoint.X < bndExtents.MinPoint.X || blockExtents.MinPoint.X > bndExtents.MaxPoint.X ||
                blockExtents.MaxPoint.Y < bndExtents.MinPoint.Y || blockExtents.MinPoint.Y > bndExtents.MaxPoint.Y)
            {
                return ContainmentResult.Outside;
            }

            Point3d p1 = blockExtents.MinPoint;
            Point3d p2 = new Point3d(blockExtents.MinPoint.X, blockExtents.MaxPoint.Y, blockExtents.MinPoint.Z);
            Point3d p3 = blockExtents.MaxPoint;
            Point3d p4 = new Point3d(blockExtents.MaxPoint.X, blockExtents.MinPoint.Y, blockExtents.MinPoint.Z);

            bool in1 = IsPointInsideCurve(boundaryCurve, boundaryVertices, p1);
            bool in2 = IsPointInsideCurve(boundaryCurve, boundaryVertices, p2);
            bool in3 = IsPointInsideCurve(boundaryCurve, boundaryVertices, p3);
            bool in4 = IsPointInsideCurve(boundaryCurve, boundaryVertices, p4);

            if (in1 && in2 && in3 && in4)
            {
                return ContainmentResult.Inside;
            }
            else if (!in1 && !in2 && !in3 && !in4)
            {
                Point3d bndCenter = new Point3d(
                    (bndExtents.MinPoint.X + bndExtents.MaxPoint.X) / 2.0,
                    (bndExtents.MinPoint.Y + bndExtents.MaxPoint.Y) / 2.0,
                    (bndExtents.MinPoint.Z + bndExtents.MaxPoint.Z) / 2.0
                );

                if (bndCenter.X >= blockExtents.MinPoint.X && bndCenter.X <= blockExtents.MaxPoint.X &&
                    bndCenter.Y >= blockExtents.MinPoint.Y && bndCenter.Y <= blockExtents.MaxPoint.Y)
                {
                    return ContainmentResult.Crossing;
                }

                foreach (Point2d vertex in boundaryVertices)
                {
                    if (vertex.X >= blockExtents.MinPoint.X && vertex.X <= blockExtents.MaxPoint.X &&
                        vertex.Y >= blockExtents.MinPoint.Y && vertex.Y <= blockExtents.MaxPoint.Y)
                    {
                        return ContainmentResult.Crossing;
                    }
                }

                return ContainmentResult.Outside;
            }
            else
            {
                return ContainmentResult.Crossing;
            }
        }

        public static List<Point2d> TessellateCurve(Curve curve, int segments = 300)
        {
            List<Point2d> points = new List<Point2d>();
            try
            {
                double startParam = curve.StartParam;
                double endParam = curve.EndParam;
                double step = (endParam - startParam) / segments;

                for (int i = 0; i <= segments; i++)
                {
                    double t = startParam + step * i;
                    if (t > endParam) t = endParam;

                    Point3d pt = curve.GetPointAtParameter(t);
                    points.Add(new Point2d(pt.X, pt.Y));
                }
            }
            catch (System.Exception ex)
            {
                Log($"경로 샘플링 오류: {ex.Message}. 경계박스 대체.");
                try
                {
                    Extents3d ext = curve.GeometricExtents;
                    points.Add(new Point2d(ext.MinPoint.X, ext.MinPoint.Y));
                    points.Add(new Point2d(ext.MinPoint.X, ext.MaxPoint.Y));
                    points.Add(new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y));
                    points.Add(new Point2d(ext.MaxPoint.X, ext.MinPoint.Y));
                }
                catch { }
            }
            return points;
        }

        public static bool IsPointInsideCurve(Curve boundaryCurve, List<Point2d> boundaryVertices, Point3d pt, double tolerance = 1e-5)
        {
            try
            {
                Extents3d extents = boundaryCurve.GeometricExtents;
                if (pt.X < extents.MinPoint.X - tolerance || pt.X > extents.MaxPoint.X + tolerance ||
                    pt.Y < extents.MinPoint.Y - tolerance || pt.Y > extents.MaxPoint.Y + tolerance)
                {
                    return false;
                }

                Point3d closest = boundaryCurve.GetClosestPointTo(pt, false);
                if (closest.DistanceTo(pt) < tolerance)
                {
                    return true;
                }

                return IsPointInPolygon(boundaryVertices, new Point2d(pt.X, pt.Y));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPointInPolygon(List<Point2d> polygon, Point2d point)
        {
            bool isInside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                if (((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)) &&
                    (point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X))
                {
                    isInside = !isInside;
                }
            }
            return isInside;
        }

        private class CandidateGroup
        {
            public string Key { get; set; } = "";
            public List<ObjectId> Ids { get; set; } = new List<ObjectId>();
            public double Width { get; set; }
            public double Height { get; set; }
        }

        private static void AddToGroups(List<CandidateGroup> groups, string key, ObjectId id, double w, double h)
        {
            double tolerance = 0.05; // 5% 크기 편차 허용
            
            foreach (var g in groups)
            {
                if (g.Key == key && 
                    Math.Abs(g.Width - w) / g.Width < tolerance && 
                    Math.Abs(g.Height - h) / g.Height < tolerance)
                {
                    g.Ids.Add(id);
                    return;
                }
            }

            groups.Add(new CandidateGroup
            {
                Key = key,
                Ids = new List<ObjectId> { id },
                Width = w,
                Height = h
            });
        }

        /// <summary>
        /// 도면 전체에서 도곽으로 유력한 닫힌 폴리선 또는 블록들을 자동으로 검색하여 탐색합니다.
        /// </summary>
        public static List<ObjectId> AutoDetectBoundaries()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            List<ObjectId> boundaryIds = new List<ObjectId>();
            List<CandidateGroup> groups = new List<CandidateGroup>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId entId in modelSpace)
                {
                    DBObject obj = tr.GetObject(entId, OpenMode.ForRead);

                    // 1. 블록 스캔
                    if (obj is BlockReference blockRef)
                    {
                        string blockName = blockRef.Name;
                        try
                        {
                            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
                            blockName = btr.Name;
                        }
                        catch { }

                        try
                        {
                            Extents3d ext = blockRef.GeometricExtents;
                            double w = ext.MaxPoint.X - ext.MinPoint.X;
                            double h = ext.MaxPoint.Y - ext.MinPoint.Y;

                            // 크기가 3000mm 이상인 실질적인 도곽 블록 위주로 탐색
                            if (w > 3000 && h > 3000)
                            {
                                string key = $"BLOCK:{blockName}";
                                AddToGroups(groups, key, entId, w, h);
                            }
                        }
                        catch { }
                    }
                    // 2. 닫힌 폴리선/곡선 스캔
                    else if (obj is Curve curve)
                    {
                        if (curve.Closed)
                        {
                            try
                            {
                                Extents3d ext = curve.GeometricExtents;
                                double w = ext.MaxPoint.X - ext.MinPoint.X;
                                double h = ext.MaxPoint.Y - ext.MinPoint.Y;

                                // 크기가 3000mm 이상인 닫힌 외곽선 위주로 탐색
                                if (w > 3000 && h > 3000)
                                {
                                    string key = $"CURVE:{curve.Layer}";
                                    AddToGroups(groups, key, entId, w, h);
                                }
                            }
                            catch { }
                        }
                    }
                }
                tr.Commit();
            }

            if (groups.Count == 0) return boundaryIds;

            // 크기와 개수를 종합한 점수제 (Score = 개수 * 면적)로 대표 도곽군 선정
            CandidateGroup? bestGroup = null;
            double maxScore = 0;

            foreach (var group in groups)
            {
                int count = group.Ids.Count;
                double area = group.Width * group.Height;
                double score = count * area;

                if (score > maxScore)
                {
                    maxScore = score;
                    bestGroup = group;
                }
            }

            if (bestGroup != null)
            {
                boundaryIds.AddRange(bestGroup.Ids);
            }

            return boundaryIds;
        }

        /// <summary>
        /// 도면 전체에서 기준 도곽과 동일한 형태(레이어 + 크기 매칭 혹은 블록 이름 일치)의 도곽 목록을 탐색합니다.
        /// </summary>
        public static List<ObjectId> DetectBoundaries(ObjectId referenceBoundaryId)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            
            List<ObjectId> boundaryIds = new List<ObjectId>();

            string targetBlockName = "";
            string targetLayerName = "";
            bool isBlockType = false;
            double targetWidth = 0;
            double targetHeight = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject obj = tr.GetObject(referenceBoundaryId, OpenMode.ForRead);
                if (obj is BlockReference blockRef)
                {
                    isBlockType = true;
                    targetBlockName = blockRef.Name;
                    try
                    {
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
                        targetBlockName = btr.Name;
                    }
                    catch { }

                    try
                    {
                        Extents3d ext = blockRef.GeometricExtents;
                        targetWidth = ext.MaxPoint.X - ext.MinPoint.X;
                        targetHeight = ext.MaxPoint.Y - ext.MinPoint.Y;
                    }
                    catch { }
                }
                else if (obj is Curve curve)
                {
                    targetLayerName = curve.Layer;
                    try
                    {
                        Extents3d ext = curve.GeometricExtents;
                        targetWidth = ext.MaxPoint.X - ext.MinPoint.X;
                        targetHeight = ext.MaxPoint.Y - ext.MinPoint.Y;
                    }
                    catch { }
                }
                else
                {
                    return boundaryIds;
                }
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId entId in modelSpace)
                {
                    DBObject obj = tr.GetObject(entId, OpenMode.ForRead);
                    
                    if (isBlockType && obj is BlockReference blockRef)
                    {
                        string blockName = blockRef.Name;
                        try
                        {
                            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
                            blockName = btr.Name;
                        }
                        catch { }

                        if (blockName == targetBlockName)
                        {
                            boundaryIds.Add(entId);
                        }
                    }
                    else if (!isBlockType && obj is Curve curve)
                    {
                        if (curve.Closed && curve.Layer == targetLayerName)
                        {
                            try
                            {
                                Extents3d ext = curve.GeometricExtents;
                                double w = ext.MaxPoint.X - ext.MinPoint.X;
                                double h = ext.MaxPoint.Y - ext.MinPoint.Y;

                                double tolerance = 0.05; // 5% 오차 허용
                                if (targetWidth > 0 && targetHeight > 0)
                                {
                                    if (Math.Abs(w - targetWidth) / targetWidth < tolerance &&
                                        Math.Abs(h - targetHeight) / targetHeight < tolerance)
                                    {
                                        boundaryIds.Add(entId);
                                    }
                                }
                                else
                                {
                                    boundaryIds.Add(entId);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                tr.Commit();
            }

            return boundaryIds;
        }

        private static void ZoomToExtents(Editor ed, Extents3d ext)
        {
            try
            {
                using (ViewTableRecord view = ed.GetCurrentView())
                {
                    Matrix3d wto = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                                    Matrix3d.Displacement(view.Target - Point3d.Origin) *
                                    Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();

                    Extents3d tempExt = ext;
                    tempExt.TransformBy(wto);

                    double width = tempExt.MaxPoint.X - tempExt.MinPoint.X;
                    double height = tempExt.MaxPoint.Y - tempExt.MinPoint.Y;
                    Point2d center = new Point2d(tempExt.MinPoint.X + width / 2.0, tempExt.MinPoint.Y + height / 2.0);

                    view.Height = height * 1.05; // 5% margin
                    view.Width = width * 1.05;
                    view.CenterPoint = center;

                    ed.SetCurrentView(view);
                }
                ed.UpdateScreen();
            }
            catch { }
        }

        /// <summary>
        /// 다중 도곽 DWG 추출 기능 (사용자 개별 저장 파일명 지정)
        /// </summary>
        public static void ExtractDrawingsByBoundariesCustomNames(
            List<ObjectId> boundaryIds, 
            List<string> customNames, 
            string outputFolder)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            Log("다중 도곽 DWG 추출 작업을 시작합니다...");

            int successCount = 0;

            for (int i = 0; i < boundaryIds.Count; i++)
            {
                ObjectId boundaryId = boundaryIds[i];
                string filePrefix = customNames[i];
                Extents3d extents;
                
                string targetBlockName = "";
                string targetLayerName = "";
                bool isBlockType = false;

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Entity ent = (Entity)tr.GetObject(boundaryId, OpenMode.ForRead);
                    try
                    {
                        extents = ent.GeometricExtents;
                    }
                    catch (System.Exception ex)
                    {
                        Log($"[경고] {i + 1}번째 도곽의 크기 계산 실패. 스킵합니다: {ex.Message}");
                        continue;
                    }

                    if (ent is BlockReference blockRef)
                    {
                        isBlockType = true;
                        targetBlockName = blockRef.Name;
                        try
                        {
                            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
                            targetBlockName = btr.Name;
                        }
                        catch { }
                    }
                    else if (ent is Curve curve)
                    {
                        targetLayerName = curve.Layer;
                    }
                    tr.Commit();
                }

                // 무조건 좌측 하단 MinPoint를 Wblock BasePoint로 설정! (0,0 좌표 정렬 오류 해결)
                Point3d basePoint = extents.MinPoint;

                // Zoom to the boundary to bring its contents into viewport for visual feedback
                ZoomToExtents(ed, extents);

                Log($"[{i + 1}/{boundaryIds.Count}] 도곽 범위 내 객체 수집 중... 범위: Min={extents.MinPoint}, Max={extents.MaxPoint}");
                
                ObjectIdCollection idsToCopy = new ObjectIdCollection();

                // 100% Reliable Database-Level Selection (Bypasses viewport/graphics engine visibility limit)
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    
                    foreach (ObjectId entId in ms)
                    {
                        if (entId == boundaryId)
                        {
                            idsToCopy.Add(entId);
                            continue;
                        }
                        
                        Entity ent = (Entity)tr.GetObject(entId, OpenMode.ForRead);
                        try
                        {
                            Extents3d entExt = ent.GeometricExtents;
                            // Check if entity extents overlap with boundary extents
                            if (!(entExt.MinPoint.X > extents.MaxPoint.X || entExt.MaxPoint.X < extents.MinPoint.X ||
                                  entExt.MinPoint.Y > extents.MaxPoint.Y || entExt.MaxPoint.Y < extents.MinPoint.Y))
                            {
                                idsToCopy.Add(entId);
                            }
                        }
                        catch { }
                    }
                    tr.Commit();
                }

                if (idsToCopy.Count == 0)
                {
                    Log($"[경고] {i + 1}번째 도곽 영역 내에 객체가 없어 스킵합니다.");
                    continue;
                }

                string safeFileName = $"{filePrefix}.dwg";
                if (!safeFileName.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                {
                    safeFileName += ".dwg";
                }
                string outPath = System.IO.Path.Combine(outputFolder, safeFileName);

                try
                {
                    using (Database destDb = db.Wblock(idsToCopy, basePoint))
                    {
                        using (Transaction destTr = destDb.TransactionManager.StartTransaction())
                        {
                            BlockTable destBt = (BlockTable)destTr.GetObject(destDb.BlockTableId, OpenMode.ForRead);
                            BlockTableRecord destMs = (BlockTableRecord)destTr.GetObject(destBt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                            ObjectId destBoundaryId = ObjectId.Null;
                            
                            foreach (ObjectId destEntId in destMs)
                            {
                                Entity destEnt = (Entity)destTr.GetObject(destEntId, OpenMode.ForRead);
                                if (isBlockType && destEnt is BlockReference destBr)
                                {
                                    string name = destBr.Name;
                                    try 
                                    {
                                        BlockTableRecord btr = (BlockTableRecord)destTr.GetObject(destBr.BlockTableRecord, OpenMode.ForRead);
                                        name = btr.Name;
                                    }
                                    catch { }

                                    // Wblock 평행 이동으로 인해 좌측 하단 MinPoint가 0,0,0 부근에 위치함
                                    if (name == targetBlockName)
                                    {
                                        try
                                        {
                                            Extents3d destExt = destEnt.GeometricExtents;
                                            if (destExt.MinPoint.DistanceTo(Point3d.Origin) < 1.0)
                                            {
                                                destBoundaryId = destEntId;
                                                break;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                else if (!isBlockType && destEnt is Curve destCurve)
                                {
                                    if (destCurve.Closed && destCurve.Layer == targetLayerName)
                                    {
                                        try
                                        {
                                            Extents3d destExt = destCurve.GeometricExtents;
                                            if (destExt.MinPoint.DistanceTo(Point3d.Origin) < 1.0)
                                            {
                                                destBoundaryId = destEntId;
                                                break;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }

                            destTr.Commit();

                            if (destBoundaryId != ObjectId.Null)
                            {
                                CropAndCleanForDatabase(destDb, destBoundaryId);
                            }
                            else
                            {
                                Log($"[경고] 복제 도면 내에서 기준 도곽 객체를 식별할 수 없어 자동 자르기를 생략합니다.");
                            }
                        }

                        destDb.SaveAs(outPath, DwgVersion.Current);
                    }

                    Log($"[성공] 도면 추출 완료: {safeFileName}");
                    successCount++;
                }
                catch (System.Exception ex)
                {
                    Log($"[오류] {i + 1}번째 도곽 추출 중 예외 발생: {ex.Message}");
                }
            }

            Log($"다중 도곽 추출 완료! 총 {boundaryIds.Count}개 중 {successCount}개 파일 저장 성공.");
        }

        /// <summary>
        /// 특정 데이터베이스 내부에서 자르기 및 정리 작업을 수행하는 헬퍼 함수
        /// </summary>
        private static void CropAndCleanForDatabase(Database db, ObjectId boundaryId)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Curve? boundaryCurve = tr.GetObject(boundaryId, OpenMode.ForRead) as Curve;
                if (boundaryCurve == null || !boundaryCurve.Closed) return;

                Extents3d bndExtents = boundaryCurve.GeometricExtents;
                List<Point2d> boundaryVertices = TessellateCurve(boundaryCurve, 300);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                List<ObjectId> entitiesToDelete = new List<ObjectId>();
                List<Entity> dbResidentEntities = new List<Entity>();
                List<BlockReference> blocksToProcess = new List<BlockReference>();

                foreach (ObjectId entId in modelSpace)
                {
                    if (entId == boundaryId) continue;

                    Entity ent = (Entity)tr.GetObject(entId, OpenMode.ForRead);
                    try
                    {
                        Extents3d entExtents = ent.GeometricExtents;
                        if (entExtents.MaxPoint.X < bndExtents.MinPoint.X || entExtents.MinPoint.X > bndExtents.MaxPoint.X ||
                            entExtents.MaxPoint.Y < bndExtents.MinPoint.Y || entExtents.MinPoint.Y > bndExtents.MaxPoint.Y)
                        {
                            entitiesToDelete.Add(entId);
                            continue;
                        }
                    }
                    catch { }

                    if (ent is BlockReference blockRef)
                    {
                        blocksToProcess.Add(blockRef);
                    }
                    else
                    {
                        dbResidentEntities.Add(ent);
                    }
                }

                List<Entity> explodedList = new List<Entity>();
                foreach (BlockReference blockRef in blocksToProcess)
                {
                    ContainmentResult relation = CheckBlockContainment(blockRef, boundaryCurve, boundaryVertices);
                    if (relation == ContainmentResult.Outside)
                    {
                        entitiesToDelete.Add(blockRef.Id);
                    }
                    else if (relation == ContainmentResult.Inside)
                    {
                        // Keep
                    }
                    else
                    {
                        List<Entity> subEntities = ExplodeBlockRecursive(blockRef, boundaryCurve, boundaryVertices, modelSpace, tr);
                        explodedList.AddRange(subEntities);
                        entitiesToDelete.Add(blockRef.Id);
                    }
                }

                int curvesTrimmed = 0;
                int dbObjectsDeleted = 0;
                int dbObjectsKept = 0;

                foreach (Entity ent in dbResidentEntities)
                {
                    ProcessEntity(ent, boundaryCurve, boundaryVertices, modelSpace, tr, entitiesToDelete, ref curvesTrimmed, ref dbObjectsDeleted, ref dbObjectsKept);
                }

                foreach (Entity ent in explodedList)
                {
                    ProcessEntity(ent, boundaryCurve, boundaryVertices, modelSpace, tr, entitiesToDelete, ref curvesTrimmed, ref dbObjectsDeleted, ref dbObjectsKept);
                }

                foreach (ObjectId delId in entitiesToDelete)
                {
                    if (delId.IsValid && !delId.IsErased)
                    {
                        DBObject obj = tr.GetObject(delId, OpenMode.ForWrite);
                        obj.Erase();
                    }
                }

                tr.Commit();
            }
        }
    }
}
