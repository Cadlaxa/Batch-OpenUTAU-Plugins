using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;
using Serilog;

namespace TransferPitchToPitd;

public struct SavedVibrato {
    public float length, period, depth, @in, @out, shift, drift, volLink;
}

public class TransferPitchToPitdCommand : PartCommand {
    Dictionary<UNote, List<PitchPoint>> oldPitches = new();
    Dictionary<UNote, List<PitchPoint>> newPitches = new();
    Dictionary<UNote, SavedVibrato> oldVibratos = new();
    
    UCurve pitdCurve;
    List<int> oldCurveXs;
    List<int> oldCurveYs;
    List<int> newCurveXs;
    List<int> newCurveYs;

    UCurve dynCurve;
    List<int> oldDynXs;
    List<int> oldDynYs;
    List<int> newDynXs;
    List<int> newDynYs;

    public TransferPitchToPitdCommand(UProject project, UVoicePart part, UCurve pitdCurve, UCurve dynCurve) : base(project, part) {
        this.pitdCurve = pitdCurve;
        this.oldCurveXs = new List<int>(pitdCurve.xs);
        this.oldCurveYs = new List<int>(pitdCurve.ys);

        this.dynCurve = dynCurve;
        if (dynCurve != null) {
            this.oldDynXs = new List<int>(dynCurve.xs);
            this.oldDynYs = new List<int>(dynCurve.ys);
        }
    }

    public void AddNoteState(UNote note) {
        oldPitches[note] = note.pitch.data.Select(p => new PitchPoint(p.X, p.Y, p.shape)).ToList();
        
        newPitches[note] = new List<PitchPoint> {
            new PitchPoint(-25f, 0f, PitchPointShape.io),
            new PitchPoint(0f, 0f, PitchPointShape.io)
        };

        var v = note.vibrato;
        oldVibratos[note] = new SavedVibrato { 
            length = v.length, period = v.period, depth = v.depth, 
            @in = v.@in, @out = v.@out, shift = v.shift, drift = v.drift, volLink = v.volLink
        };
    }

    public void SetNewCurveData(List<int> pXs, List<int> pYs, List<int> dXs, List<int> dYs) {
        newCurveXs = pXs;
        newCurveYs = pYs;
        newDynXs = dXs;
        newDynYs = dYs;
    }

    public override void Execute() {
        foreach (var kvp in newPitches) {
            kvp.Key.pitch.data.Clear();
            kvp.Key.pitch.data.AddRange(kvp.Value);
            kvp.Key.vibrato.length = 0; 
            kvp.Key.vibrato.depth = 0;
            kvp.Key.vibrato.volLink = 0;
        }
        
        pitdCurve.xs.Clear();
        pitdCurve.ys.Clear();
        pitdCurve.xs.AddRange(newCurveXs);
        pitdCurve.ys.AddRange(newCurveYs);

        if (dynCurve != null && newDynXs != null) {
            dynCurve.xs.Clear();
            dynCurve.ys.Clear();
            dynCurve.xs.AddRange(newDynXs);
            dynCurve.ys.AddRange(newDynYs);
        }
    }

    public override void Unexecute() {
        foreach (var kvp in oldPitches) {
            kvp.Key.pitch.data.Clear();
            kvp.Key.pitch.data.AddRange(kvp.Value);
        }
        foreach (var kvp in oldVibratos) {
            var target = kvp.Key.vibrato;
            var src = kvp.Value;
            target.length = src.length;
            target.period = src.period;
            target.depth = src.depth;
            target.@in = src.@in;
            target.@out = src.@out;
            target.shift = src.shift;
            target.drift = src.drift;
            target.volLink = src.volLink;
        }

        pitdCurve.xs.Clear();
        pitdCurve.ys.Clear();
        pitdCurve.xs.AddRange(oldCurveXs);
        pitdCurve.ys.AddRange(oldCurveYs);

        if (dynCurve != null && oldDynXs != null) {
            dynCurve.xs.Clear();
            dynCurve.ys.Clear();
            dynCurve.xs.AddRange(oldDynXs);
            dynCurve.ys.AddRange(oldDynYs);
        }
    }

    public override string ToString() => "Convert Pitch & Vibrato to PITD and DYN";
}

public class TransferPitchToPitdPlugin : BatchEdit {
    public virtual string Name => "Convert Pitch & Vibrato to PITD and DYN";

    private double GetShapeWeight(double t, PitchPointShape shape) {
        switch (shape) {
            case PitchPointShape.i: return 1.0 - Math.Cos(t * Math.PI / 2.0); 
            case PitchPointShape.o: return Math.Sin(t * Math.PI / 2.0);       
            case PitchPointShape.io: return 0.5 * (1.0 - Math.Cos(t * Math.PI)); 
            case PitchPointShape.l: default: return t;                        
        }
    }

    public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
        var notes = selectedNotes.Count > 0 ? selectedNotes : part.notes.ToList();
        if (notes.Count == 0) return;

        var pitdCurve = part.curves.FirstOrDefault(c => c.abbr == "pitd");
        if (pitdCurve == null) {
            if (project.expressions.TryGetValue("pitd", out var pitdDesc)) {
                pitdCurve = new UCurve(pitdDesc);
                part.curves.Add(pitdCurve);
            } else {
                Log.Error("[TransferPitchToPitd] Could not find or create PITD expression.");
                return;
            }
        }

        var dynCurve = part.curves.FirstOrDefault(c => c.abbr == "dyn");
        if (dynCurve == null) {
            if (project.expressions.TryGetValue("dyn", out var dynDesc)) {
                dynCurve = new UCurve(dynDesc);
                part.curves.Add(dynCurve);
            }
        }

        var command = new TransferPitchToPitdCommand(project, part, pitdCurve, dynCurve);
        
        List<int> tempXs = new List<int>(pitdCurve.xs);
        List<int> tempYs = new List<int>(pitdCurve.ys);
        List<int> tempDynXs = dynCurve != null ? new List<int>(dynCurve.xs) : null;
        List<int> tempDynYs = dynCurve != null ? new List<int>(dynCurve.ys) : null;

        int sampleInterval = 1;

        foreach (var note in notes) {
            command.AddNoteState(note); 

            double noteStartMs = project.timeAxis.TickPosToMsPos(note.position);
            double noteEndMs = project.timeAxis.TickPosToMsPos(note.position + note.duration);
            double noteMs = noteEndMs - noteStartMs;

            float minMs = -40f; 
            float maxMs = (float)noteMs + 40f;

            if (note.pitch.data.Count > 0) {
                minMs = Math.Min(minMs, note.pitch.data.Min(p => p.X));
                maxMs = Math.Max(maxMs, note.pitch.data.Max(p => p.X));
            }

            int startTick = project.timeAxis.MsPosToTickPos(noteStartMs + minMs);
            int endTick = project.timeAxis.MsPosToTickPos(noteStartMs + maxMs);

            double vLengthMs = noteMs * (note.vibrato.length / 100.0);
            double vStartMs = noteMs - vLengthMs;
            bool hasVolLink = dynCurve != null && note.vibrato.length > 0 && note.vibrato.volLink != 0;

            for (int i = tempXs.Count - 1; i >= 0; i--) {
                if (tempXs[i] >= startTick && tempXs[i] <= endTick) {
                    tempXs.RemoveAt(i);
                    tempYs.RemoveAt(i);
                }
            }

            if (hasVolLink) {
                for (int i = tempDynXs.Count - 1; i >= 0; i--) {
                    if (tempDynXs[i] >= startTick && tempDynXs[i] <= endTick) {
                        tempDynXs.RemoveAt(i);
                        tempDynYs.RemoveAt(i);
                    }
                }
            }

            for (int t = startTick; t <= endTick; t += sampleInterval) {
                double currentMs = project.timeAxis.TickPosToMsPos(t) - noteStartMs;
                
                float currentY = 0;
                if (note.pitch.data.Count > 1) {
                    var sortedPitches = note.pitch.data.OrderBy(p => p.X).ToList();
                    if (currentMs <= sortedPitches.First().X) {
                        currentY = sortedPitches.First().Y;
                    } else if (currentMs >= sortedPitches.Last().X) {
                        currentY = sortedPitches.Last().Y;
                    } else {
                        for (int i = 0; i < sortedPitches.Count - 1; i++) {
                            var p1 = sortedPitches[i];
                            var p2 = sortedPitches[i + 1];
                            if (currentMs >= p1.X && currentMs <= p2.X) {
                                double ratio = (currentMs - p1.X) / (p2.X - p1.X);
                                currentY = (float)(p1.Y + (p2.Y - p1.Y) * GetShapeWeight(ratio, p1.shape));
                                break;
                            }
                        }
                    }
                }

                float pitchCents = currentY * 10f;
                if (currentMs < 0) {
                    var prevNote = part.notes.LastOrDefault(n => n.position < note.position);
                    if (prevNote != null) {
                        float diffCents = (note.tone - prevNote.tone) * 100f;
                        if (currentMs <= -25) {
                            pitchCents += diffCents;
                        } else {
                            // Cancel out the native 'io' glide over the final -25ms window
                            double ratio = (currentMs - (-25)) / 25.0;
                            double weight = 0.5 * (1.0 - Math.Cos(ratio * Math.PI));
                            pitchCents += diffCents * (float)(1.0 - weight);
                        }
                    }
                }
                // -------------------------------

                float vibratoCents = 0;
                float dynVibrato = 0;
                if (note.vibrato.length > 0 && note.vibrato.period > 0 && currentMs >= vStartMs && currentMs <= noteMs) {
                    double vt = currentMs - vStartMs;
                    double env = 1.0;
                    double inMs = vLengthMs * (note.vibrato.@in / 100.0);
                    double outMs = vLengthMs * (note.vibrato.@out / 100.0);

                    if (inMs > 0 && vt < inMs) env = vt / inMs;
                    else if (outMs > 0 && vt > vLengthMs - outMs) env = (vLengthMs - vt) / outMs;

                    double phaseOffset = 2 * Math.PI * (note.vibrato.shift / 100.0);
                    double pureSine = env * Math.Sin(2 * Math.PI * (vt / note.vibrato.period) + phaseOffset);
                    vibratoCents = (float)(note.vibrato.depth * pureSine + env * note.vibrato.depth * (note.vibrato.drift / 100.0));
                    if (hasVolLink) dynVibrato = (float)(note.vibrato.volLink * pureSine * 0.6);
                }

                tempXs.Add(t);
                tempYs.Add((int)Math.Round(pitchCents + vibratoCents));
                if (hasVolLink) {
                    tempDynXs.Add(t);
                    tempDynYs.Add((int)Math.Round(dynCurve.Sample(t) + dynVibrato));
                }
            }
        }

        var sortedPitd = tempXs.Zip(tempYs, (x, y) => new { x, y }).OrderBy(item => item.x).DistinctBy(i => i.x).ToList();
        List<int> finalDynXs = null;
        List<int> finalDynYs = null;
        if (tempDynXs != null) {
            var sortedDyn = tempDynXs.Zip(tempDynYs, (x, y) => new { x, y }).OrderBy(item => item.x).DistinctBy(i => i.x).ToList();
            finalDynXs = sortedDyn.Select(i => i.x).ToList();
            finalDynYs = sortedDyn.Select(i => i.y).ToList();
        }

        command.SetNewCurveData(sortedPitd.Select(i => i.x).ToList(), sortedPitd.Select(i => i.y).ToList(), finalDynXs, finalDynYs);

        docManager.StartUndoGroup("command.batch.transfer_pitch_to_pitd", true);
        docManager.ExecuteCmd(command);
        docManager.EndUndoGroup();
        Log.Information("[TransferPitchToPitd] High-precision transfer with accurate note transition correction.");
    }
}