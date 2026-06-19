using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AutoPublicTransit
{
    public class LineDeletionResult
    {
        public TransportInfo.TransportType TransportType;
        public int MatchedLines;
        public int DeletedLines;
        public int FailedLines;
        public int Passes;
        public int ProtectedLinesSkipped;
        public int RemainingVisibleLines;
        public int RemainingCandidateLines;
        public bool CancelledActiveScan;
    }

    public partial class Manager
    {
        private const int DeleteAllLinesMaxPasses = 16;
        private const int DeleteAllLinesDeferredCleanupSweeps = 8;
        private bool _deleteAllBusCleanupRunning;

        public int CountDeletableLines(TransportInfo.TransportType transportType)
        {
            TransportManager tm = TransportManager.instance;
            if (tm == null)
                return 0;

            int count = 0;
            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if (IsDeletableLine(lineId, ref line, transportType, false))
                    count++;
            }

            return count;
        }

        public LineDeletionResult DeleteAllLines(TransportInfo.TransportType transportType)
        {
            var result = new LineDeletionResult();
            result.TransportType = transportType;

            TransportManager tm = TransportManager.instance;
            if (tm == null)
                return result;

            result.CancelledActiveScan = CancelActiveScanForDeleteAllLines(transportType);
            bool includeHiddenOrTemporary = transportType == TransportInfo.TransportType.Bus;
            for (int pass = 1; pass <= DeleteAllLinesMaxPasses; pass++)
            {
                int protectedLines;
                int visibleLines;
                int hiddenOrTemporaryLines;
                List<ushort> lineIds = GetLineDeletionCandidateIds(
                    tm,
                    transportType,
                    includeHiddenOrTemporary,
                    out protectedLines,
                    out visibleLines,
                    out hiddenOrTemporaryLines);
                if (lineIds.Count == 0)
                {
                    result.ProtectedLinesSkipped = protectedLines;
                    break;
                }

                result.Passes++;
                result.MatchedLines += lineIds.Count;
                result.ProtectedLinesSkipped = protectedLines;
                TransitLogging.Log(
                    "Delete all " + transportType + " pass " + pass +
                    ": candidates=" + lineIds.Count +
                    ", visible=" + visibleLines +
                    ", hiddenOrTemporary=" + hiddenOrTemporaryLines +
                    ", protectedSkipped=" + protectedLines + ".");

                ReleaseLineDeletionCandidates(tm, transportType, lineIds, ref result.DeletedLines, ref result.FailedLines);
            }

            result.RemainingVisibleLines = CountDeletableLines(transportType);
            result.RemainingCandidateLines = CountLineDeletionCandidates(tm, transportType, includeHiddenOrTemporary, out result.ProtectedLinesSkipped);
            LogLineDeletionSurvivors(tm, transportType, includeHiddenOrTemporary, "immediate");
            if (result.DeletedLines > 0)
            {
                State.ClearTransient();
                RefreshPublicTransportOverviewPanels("line-tools delete all " + transportType, true);
                StartCoroutine(RefreshPublicTransportOverviewPanelsDeferred());
                StartDeleteAllLinesDeferredCleanup(transportType, includeHiddenOrTemporary);
            }

            TransitLogging.Log(
                "Delete all lines completed for " + transportType +
                ": matched=" + result.MatchedLines +
                ", deleted=" + result.DeletedLines +
                ", failed=" + result.FailedLines +
                ", passes=" + result.Passes +
                ", cancelledActiveScan=" + result.CancelledActiveScan +
                ", protectedSkipped=" + result.ProtectedLinesSkipped +
                ", remainingVisible=" + result.RemainingVisibleLines +
                ", remainingCandidates=" + result.RemainingCandidateLines + ".");

            return result;
        }

        private void StartDeleteAllLinesDeferredCleanup(TransportInfo.TransportType transportType, bool includeHiddenOrTemporary)
        {
            if (transportType != TransportInfo.TransportType.Bus || _deleteAllBusCleanupRunning)
                return;

            _deleteAllBusCleanupRunning = true;
            StartCoroutine(DeleteAllLinesDeferredCleanup(transportType, includeHiddenOrTemporary));
        }

        private IEnumerator DeleteAllLinesDeferredCleanup(TransportInfo.TransportType transportType, bool includeHiddenOrTemporary)
        {
            int matched = 0;
            int deleted = 0;
            int failed = 0;
            int sweeps = 0;

            for (int sweep = 1; sweep <= DeleteAllLinesDeferredCleanupSweeps; sweep++)
            {
                yield return null;
                yield return null;

                TransportManager tm = TransportManager.instance;
                if (tm == null)
                    break;

                int protectedLines;
                int visibleLines;
                int hiddenOrTemporaryLines;
                List<ushort> lineIds = GetLineDeletionCandidateIds(
                    tm,
                    transportType,
                    includeHiddenOrTemporary,
                    out protectedLines,
                    out visibleLines,
                    out hiddenOrTemporaryLines);
                if (lineIds.Count == 0)
                {
                    TransitLogging.Log(
                        "Deferred delete-all cleanup completed for " + transportType +
                        ": sweeps=" + sweeps +
                        ", matched=" + matched +
                        ", deleted=" + deleted +
                        ", failed=" + failed +
                        ", protectedSkipped=" + protectedLines +
                        ", remainingVisible=0, remainingCandidates=0.");
                    LogProtectedLineDeletionSurvivors(tm, transportType, "deferred");
                    _deleteAllBusCleanupRunning = false;
                    yield break;
                }

                sweeps++;
                matched += lineIds.Count;
                TransitLogging.Warn(
                    "Deferred delete-all cleanup for " + transportType + " sweep " + sweep +
                    " found surviving candidate(s): candidates=" + lineIds.Count +
                    ", visible=" + visibleLines +
                    ", hiddenOrTemporary=" + hiddenOrTemporaryLines +
                    ", protectedSkipped=" + protectedLines +
                    ", lines=" + DescribeLineIds(tm, lineIds, 8) + ".");
                ReleaseLineDeletionCandidates(tm, transportType, lineIds, ref deleted, ref failed);
                State.ClearTransient();
                RefreshPublicTransportOverviewPanels("line-tools deferred delete all " + transportType, true);
            }

            TransportManager finalTm = TransportManager.instance;
            int finalProtectedLines = 0;
            int remainingCandidates = CountLineDeletionCandidates(finalTm, transportType, includeHiddenOrTemporary, out finalProtectedLines);
            int remainingVisible = CountDeletableLines(transportType);
            TransitLogging.Warn(
                "Deferred delete-all cleanup finished for " + transportType +
                ": sweeps=" + sweeps +
                ", matched=" + matched +
                ", deleted=" + deleted +
                ", failed=" + failed +
                ", protectedSkipped=" + finalProtectedLines +
                ", remainingVisible=" + remainingVisible +
                ", remainingCandidates=" + remainingCandidates + ".");
            LogLineDeletionSurvivors(finalTm, transportType, includeHiddenOrTemporary, "deferred");
            _deleteAllBusCleanupRunning = false;
        }

        private void ReleaseLineDeletionCandidates(
            TransportManager tm,
            TransportInfo.TransportType transportType,
            List<ushort> lineIds,
            ref int deleted,
            ref int failed)
        {
            for (int i = 0; i < lineIds.Count; i++)
            {
                ushort lineId = lineIds[i];
                try
                {
                    tm.ReleaseLine(lineId);
                    deleted++;
                }
                catch (Exception e)
                {
                    failed++;
                    TransitLogging.Warn("Failed to delete " + transportType + " line " + lineId + ": " + e.Message);
                }
            }
        }

        private List<ushort> GetDeletableLineIds(TransportManager tm, TransportInfo.TransportType transportType)
        {
            var lineIds = new List<ushort>();
            if (tm == null)
                return lineIds;

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if (IsDeletableLine(lineId, ref line, transportType, false))
                    lineIds.Add(lineId);
            }

            return lineIds;
        }

        private List<ushort> GetLineDeletionCandidateIds(
            TransportManager tm,
            TransportInfo.TransportType transportType,
            bool includeHiddenOrTemporary,
            out int protectedLines,
            out int visibleLines,
            out int hiddenOrTemporaryLines)
        {
            var lineIds = new List<ushort>();
            protectedLines = 0;
            visibleLines = 0;
            hiddenOrTemporaryLines = 0;
            if (tm == null)
                return lineIds;

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                bool isProtected;
                bool hiddenOrTemporary;
                if (!TryGetDeleteLineClassification(lineId, ref line, transportType, out isProtected, out hiddenOrTemporary))
                    continue;

                if (isProtected)
                {
                    protectedLines++;
                    continue;
                }

                if (hiddenOrTemporary)
                {
                    hiddenOrTemporaryLines++;
                    if (!includeHiddenOrTemporary)
                        continue;
                }
                else
                {
                    visibleLines++;
                }

                lineIds.Add(lineId);
            }

            return lineIds;
        }

        private int CountLineDeletionCandidates(
            TransportManager tm,
            TransportInfo.TransportType transportType,
            bool includeHiddenOrTemporary,
            out int protectedLines)
        {
            int visibleLines;
            int hiddenOrTemporaryLines;
            return GetLineDeletionCandidateIds(
                tm,
                transportType,
                includeHiddenOrTemporary,
                out protectedLines,
                out visibleLines,
                out hiddenOrTemporaryLines).Count;
        }

        private void LogLineDeletionSurvivors(
            TransportManager tm,
            TransportInfo.TransportType transportType,
            bool includeHiddenOrTemporary,
            string phase)
        {
            if (tm == null)
                return;

            int protectedLines;
            int visibleLines;
            int hiddenOrTemporaryLines;
            List<ushort> candidates = GetLineDeletionCandidateIds(
                tm,
                transportType,
                includeHiddenOrTemporary,
                out protectedLines,
                out visibleLines,
                out hiddenOrTemporaryLines);
            if (candidates.Count > 0)
            {
                TransitLogging.Warn(
                    "Delete all " + transportType + " " + phase +
                    " survivor candidates: visible=" + visibleLines +
                    ", hiddenOrTemporary=" + hiddenOrTemporaryLines +
                    ", lines=" + DescribeLineIds(tm, candidates, 12) + ".");
            }

            LogProtectedLineDeletionSurvivors(tm, transportType, phase);
        }

        private void LogProtectedLineDeletionSurvivors(TransportManager tm, TransportInfo.TransportType transportType, string phase)
        {
            List<ushort> protectedIds = GetProtectedLineDeletionIds(tm, transportType);
            if (protectedIds.Count == 0)
                return;

            TransitLogging.Log(
                "Delete all " + transportType + " " + phase +
                " protected line survivors: count=" + protectedIds.Count +
                ", lines=" + DescribeLineIds(tm, protectedIds, 12) + ".");
        }

        private List<ushort> GetProtectedLineDeletionIds(TransportManager tm, TransportInfo.TransportType transportType)
        {
            var lineIds = new List<ushort>();
            if (tm == null)
                return lineIds;

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                bool isProtected;
                bool hiddenOrTemporary;
                if (TryGetDeleteLineClassification(lineId, ref line, transportType, out isProtected, out hiddenOrTemporary) && isProtected)
                    lineIds.Add(lineId);
            }

            return lineIds;
        }

        private string DescribeLineIds(TransportManager tm, List<ushort> lineIds, int maxLines)
        {
            if (tm == null || lineIds == null || lineIds.Count == 0)
                return "none";

            var builder = new StringBuilder();
            int count = Math.Min(maxLines, lineIds.Count);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    builder.Append("; ");

                ushort lineId = lineIds[i];
                string name = SafeGetTransportLineName(lineId);
                if (string.IsNullOrEmpty(name))
                    name = "<unnamed>";

                TransportLine.Flags flags = 0;
                if (lineId < tm.m_lines.m_size)
                    flags = tm.m_lines.m_buffer[lineId].m_flags;

                builder.Append(lineId);
                builder.Append(" '");
                builder.Append(name);
                builder.Append("' flags=");
                builder.Append(flags);
            }

            if (lineIds.Count > count)
                builder.Append("; +" + (lineIds.Count - count) + " more");

            return builder.ToString();
        }

        private bool IsDeletableLine(ushort lineId, ref TransportLine line, TransportInfo.TransportType transportType, bool includeHiddenOrTemporary)
        {
            bool isProtected;
            bool hiddenOrTemporary;
            if (!TryGetDeleteLineClassification(lineId, ref line, transportType, out isProtected, out hiddenOrTemporary))
                return false;

            if (isProtected)
                return false;

            return includeHiddenOrTemporary || !hiddenOrTemporary;
        }

        private bool TryGetDeleteLineClassification(
            ushort lineId,
            ref TransportLine line,
            TransportInfo.TransportType transportType,
            out bool isProtected,
            out bool hiddenOrTemporary)
        {
            isProtected = false;
            hiddenOrTemporary = false;
            if ((line.m_flags & TransportLine.Flags.Created) == 0)
                return false;

            TransportInfo info = line.Info;
            if (info == null || info.m_transportType != transportType)
                return false;

            isProtected = IsProtectedFromLineTools(transportType, lineId, ref line);
            hiddenOrTemporary = (line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden)) != 0;
            return true;
        }

        private bool CancelActiveScanForDeleteAllLines(TransportInfo.TransportType transportType)
        {
            if (transportType != TransportInfo.TransportType.Bus || !_scanRunning)
                return false;

            _scanRunning = false;
            TransitLogging.Warn("Delete all Bus lines requested during an active APT scan; cancelling pending generated-line settlement before deleting ordinary bus lines.");
            if (State.LastScanSummary != null && !State.LastScanSummary.Completed)
            {
                State.LastScanSummary.FailureMessage = "Cancelled by Delete Bus.";
                AutoPublicTransitUI.UpdateScanSummary(State.LastScanSummary);
            }

            return true;
        }
    }
}
