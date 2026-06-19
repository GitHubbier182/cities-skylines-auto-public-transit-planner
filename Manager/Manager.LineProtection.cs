using System;

namespace AutoPublicTransit
{
    public partial class Manager
    {
        private bool IsPlayerProtectedLine(ushort lineId, ref TransportLine line)
        {
            if ((line.m_flags & TransportLine.Flags.Created) == 0)
                return false;

            if ((line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden)) != 0)
                return false;

            return ContainsDndMarker(SafeGetTransportLineName(lineId));
        }

        private bool IsProtectedFromAptManagement(ushort lineId, ref TransportLine line)
        {
            return IsPlayerProtectedLine(lineId, ref line) || IsProtectedSchoolBusRoute(lineId, ref line);
        }

        private bool IsProtectedFromLineTools(TransportInfo.TransportType transportType, ushort lineId, ref TransportLine line)
        {
            if (IsPlayerProtectedLine(lineId, ref line))
                return true;

            return transportType == TransportInfo.TransportType.Bus && IsProtectedSchoolBusRoute(lineId, ref line);
        }

        private bool IsManagedExistingLine(ExistingLineSnapshot line)
        {
            return line != null && !line.IsProtectedFromAptManagement;
        }

        private bool ContainsDndMarker(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            for (int i = 0; i <= value.Length - 3; i++)
            {
                if ((value[i] == 'd' || value[i] == 'D') &&
                    (value[i + 1] == 'n' || value[i + 1] == 'N') &&
                    (value[i + 2] == 'd' || value[i + 2] == 'D') &&
                    IsDndMarkerBoundary(value, i - 1) &&
                    IsDndMarkerBoundary(value, i + 3))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsDndMarkerBoundary(string value, int index)
        {
            if (index < 0 || index >= value.Length)
                return true;

            return !char.IsLetterOrDigit(value[index]);
        }
    }
}
