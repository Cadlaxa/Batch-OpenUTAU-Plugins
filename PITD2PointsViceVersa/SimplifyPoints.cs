using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;
using Serilog;

namespace SimplifyPitchPoints;

public class SimplifyPitchPointsPlugin : BatchEdit {
    public virtual string Name => "Simplify Pitch Points (Ease in/out)";

    private List<PitchPoint> SimplifyDP(List<PitchPoint> points, float epsilon) {
        if (points.Count <= 2) return points;

        double maxError = 0;
        int maxIndex = 0;

        float x1 = points.First().X;
        float y1 = points.First().Y;
        float x2 = points.Last().X;
        float y2 = points.Last().Y;

        for (int i = 1; i < points.Count - 1; i++) {
            float x0 = points[i].X;
            float y0 = points[i].Y;

            float yInt;
            if (x2 == x1) {
                yInt = y1;
            } else {
                yInt = y1 + (x0 - x1) * (y2 - y1) / (x2 - x1);
            }

            double error = Math.Abs(y0 - yInt);
            if (error > maxError) {
                maxError = error;
                maxIndex = i;
            }
        }

        if (maxError > epsilon) {
            var left = SimplifyDP(points.GetRange(0, maxIndex + 1), epsilon);
            var right = SimplifyDP(points.GetRange(maxIndex, points.Count - maxIndex), epsilon);

            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        } else {
            return new List<PitchPoint> { points.First(), points.Last() };
        }
    }

    public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
        var notes = selectedNotes.Count > 0 ? selectedNotes : part.notes.ToList();
        if (notes.Count == 0) return;

        float toleranceEpsilon = 2f; 
        
        var pendingChanges = new Dictionary<UNote, List<PitchPoint>>();

        foreach (var note in notes) {
            if (note.pitch.data.Count > 2) {
                var newPoints = SimplifyDP(note.pitch.data, toleranceEpsilon);
                
                if (newPoints.Count < note.pitch.data.Count) {
                    pendingChanges[note] = newPoints;
                }
            }
        }

        if (pendingChanges.Count > 0) {
            docManager.StartUndoGroup("command.batch.simplify_pitch", true);

            foreach (var kvp in pendingChanges) {
                var note = kvp.Key;
                var newPoints = kvp.Value;

                docManager.ExecuteCmd(new ResetPitchPointsCommand(part, note));
                
                int index = 0;
                foreach (var point in newPoints) {
                    var clonedPoint = new PitchPoint(point.X, point.Y, PitchPointShape.io);
                    docManager.ExecuteCmd(new AddPitchPointCommand(part, note, clonedPoint, index));
                    index++;
                }
                
                docManager.ExecuteCmd(new DeletePitchPointCommand(part, note, index));
                docManager.ExecuteCmd(new DeletePitchPointCommand(part, note, index));
            }

            docManager.EndUndoGroup();
            Log.Information($"[SimplifyPitchPoints] Successfully simplified pitch points on {pendingChanges.Count} notes.");
        } else {
            Log.Information("[SimplifyPitchPoints] No pitch points needed simplification.");
        }
    }
}