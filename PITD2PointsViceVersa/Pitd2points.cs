using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Editing;
using OpenUtau.Core;
using Serilog;

namespace TransferPitdToPitch;

public class BakePitchToPointsPlugin : BatchEdit {
    public virtual string Name => "Convert PITD to Pitch Points (High Density)";

    struct Point {
        public int X;
        public double Y;
        public PitchPointShape shape;
        public Point(int X, double Y, PitchPointShape shape = PitchPointShape.l) {
            this.X = X;
            this.Y = Y;
            this.shape = shape;
        }

        public Point ChangeShape(PitchPointShape shape) {
            return new Point(X, Y, shape);
        }
    }

    double deltaY(Point pt, Point lineStart, Point lineEnd, PitchPointShape shape) {
        return pt.Y - MusicMath.InterpolateShape(lineStart.X, lineEnd.X, lineStart.Y, lineEnd.Y, pt.X, shape);
    }

    PitchPointShape DetermineShape(Point start, Point middle, Point end) {
        if (start.Y == end.Y) {
            return PitchPointShape.l;
        }
        var k = (middle.Y - start.Y) / (end.Y - start.Y);
        if (k > 0.67) return PitchPointShape.o;
        if (k < 0.33) return PitchPointShape.i;
        return PitchPointShape.l;
    }

    List<Point> simplifyShape(List<Point> pointList, Double epsilon) {
        if (pointList.Count <= 2) {
            // FIX 1: Safely return start and end with proper shape calculation
            var shortShape = DetermineShape(pointList[0], pointList[pointList.Count / 2], pointList[^1]);
            return new List<Point> { pointList[0].ChangeShape(shortShape), pointList[^1] };
        }

        var startPoint = pointList[0];
        var middlePoint = pointList[pointList.Count / 2];
        var endPoint = pointList[^1];
        var shape = DetermineShape(startPoint, middlePoint, endPoint);

        var dmax = 0.0;
        var index = 0;
        var end = pointList.Count - 1;
        
        for (var i = 1; i < end; i++) {
            var d = Math.Abs(deltaY(pointList[i], startPoint, endPoint, shape));
            if (d > dmax) {
                index = i;
                dmax = d;
            }
        }

        if (dmax > epsilon) {
            var recResults1 = simplifyShape(pointList.GetRange(0, index + 1), epsilon);
            var recResults2 = simplifyShape(pointList.GetRange(index, pointList.Count - index), epsilon);

            // FIX 2: Safely remove the duplicate point at the recursion boundary
            var results = new List<Point>(recResults1);
            results.RemoveAt(results.Count - 1); 
            results.AddRange(recResults2);
            
            return results;
        } else {
            // Base case: return start and end of this simplified line
            return new List<Point> { startPoint.ChangeShape(shape), endPoint };
        }
    }

    public static int LastIndexOfMin<T>(IList<T> self, Func<T, double> selector, int startIndex, int endIndex) {
        if (self == null) throw new ArgumentNullException("self");
        if (self.Count == 0) throw new ArgumentException("List is empty.", "self");

        var min = selector(self[endIndex - 1]);
        int minIndex = endIndex - 1;

        for (int i = endIndex - 1; i >= startIndex; --i) {
            var value = selector(self[i]);
            if (value < min) {
                min = value;
                minIndex = i;
            }
        }
        return minIndex;
    }

    public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
        TimeAxis timeAxis = project.timeAxis;
        const int pitchInterval = 5;
        var notes = selectedNotes.Count > 0 ? selectedNotes : part.notes.ToList();
        var positions = notes.Select(n => n.position + part.position).ToHashSet();
        var phrases = part.renderPhrases.Where(phrase => phrase.notes.Any(n => positions.Contains(phrase.position + n.position)));
        
        var pitchPointsPerNote = new Dictionary<int, Tuple<int, int, List<PitchPoint>>>();
        
        foreach (var phrase in phrases) {
            var pitchStart = -phrase.leading;
            var pitches = phrase.pitches;
            var points = Enumerable.Zip(
                Enumerable.Range(0, pitches.Length),
                pitches,
                (i, pitch) => new Point(pitchStart + i * pitchInterval, pitch)
            ).ToList();

            var mustIncludeIndices = phrase.notes
                .SelectMany(n => new[] {
                    n.position,
                    n.duration > 160 ? n.end - 80 : n.position + n.duration / 2 })
                .Select(t => (t - pitchStart) / pitchInterval)
                .Prepend(0)
                .Append(points.Count - 1)
                .ToList();
            
            // FIX 3: Correct array slicing (b - a + 1) to include the endpoint in the calculation
            // FIX 4: Epsilon set to 0.5 for High Density (Tracks tight vibrato perfectly)
            points = mustIncludeIndices.Zip(mustIncludeIndices.Skip(1),
                    (a, b) => simplifyShape(points.GetRange(a, b - a + 1), 0.5))
                // Strip the last point of each chunk to avoid duplicate seams, then add the final point at the end
                .SelectMany(chunk => chunk.Take(chunk.Count - 1))
                .Append(points[^1])
                .ToList();

            int idx = 0;
            var note_boundaries = new int[phrase.notes.Length + 1];
            note_boundaries[0] = 2;
            foreach (int i in Enumerable.Range(0, phrase.notes.Length)) {
                var note = phrase.notes[i];
                while (idx < points.Count && points[idx].X < note.end) {
                    idx++;
                }
                note_boundaries[i + 1] = idx;
            }
            
            var adjusted_boundaries = new int[phrase.notes.Length + 1];
            adjusted_boundaries[0] = 2;
            foreach (int i in Enumerable.Range(0, phrase.notes.Length - 1)) {
                var note = phrase.notes[i];
                var notePitch = note.tone * 100;
                
                var zero_point = Enumerable.Range(0, note_boundaries[i + 1] - note_boundaries[i])
                    .Select(j => note_boundaries[i + 1] - 1 - j)
                    .Where(j => j > 0 && (points[j].Y - notePitch) * (points[j - 1].Y - notePitch) <= 0)
                    .DefaultIfEmpty(-1)
                    .First();
                    
                if (zero_point != -1) {
                    adjusted_boundaries[i + 1] = zero_point + 1;
                } else {
                    adjusted_boundaries[i + 1] = LastIndexOfMin(points, p => Math.Abs(p.Y - notePitch), note_boundaries[i], note_boundaries[i + 1]) + 2;
                }
            }
            adjusted_boundaries[^1] = note_boundaries[^1];
            
            foreach (int i in Enumerable.Range(0, phrase.notes.Length)) {
                var note = phrase.notes[i];
                // Safety clamp to prevent index crashes on extremely short notes
                int startI = Math.Max(0, adjusted_boundaries[i] - 2);
                int endI = Math.Min(points.Count, adjusted_boundaries[i + 1]);
                int rangeCount = Math.Max(0, endI - startI);

                var pitch = points.GetRange(startI, rangeCount)
                    .Select(p => new PitchPoint(
                        (float)timeAxis.MsBetweenTickPos(note.position + part.position, p.X + part.position),
                        (float)(p.Y - note.tone * 100) / 10,
                        p.shape))
                    .ToList();
                    
                pitchPointsPerNote[note.position + phrase.position - part.position] = Tuple.Create(
                    points[startI].X + phrase.position,
                    points[endI - 1].X + phrase.position,
                    pitch);
            }
        }
        
        docManager.StartUndoGroup("command.batch.note", true);
        
        foreach (var note in notes) {
            if (pitchPointsPerNote.TryGetValue(note.position, out var tickRangeAndPitch)) {
                var pitch = tickRangeAndPitch.Item3;
                docManager.ExecuteCmd(new ResetPitchPointsCommand(part, note));
                int index = 0;
                foreach (var point in pitch) {
                    docManager.ExecuteCmd(new AddPitchPointCommand(part, note, point, index));
                    index++;
                }
                docManager.ExecuteCmd(new DeletePitchPointCommand(part, note, index));
                docManager.ExecuteCmd(new DeletePitchPointCommand(part, note, index));
                
                if (note.pitch.data.Count > 0) {
                    var lastPitch = note.pitch.data[^1];
                    docManager.ExecuteCmd(new MovePitchPointCommand(part, lastPitch, 0, -lastPitch.Y));
                }
            }
        }
        
        foreach (var note in notes) {
            if (pitchPointsPerNote.TryGetValue(note.position, out var tickRangeAndPitch)) {
                var start = tickRangeAndPitch.Item1 - part.position;
                var end = tickRangeAndPitch.Item2 - part.position;
                docManager.ExecuteCmd(new PasteCurveCommand(project, part, "pitd", start, 0, end, 0));
            }
        }
        
        foreach (var note in notes) {
            if (note.vibrato.length > 0) {
                docManager.ExecuteCmd(new VibratoLengthCommand(part, note, 0));
            }
        }
        
        docManager.ExecuteCmd(new SetNotesSameExpressionCommand(DocManager.Inst.Project, project.tracks[part.trackNo], part, notes, "mod+", null));
        docManager.EndUndoGroup();
        Log.Information("[TransferPitdToPitch] Converted using High-Density RDP.");
    }
}