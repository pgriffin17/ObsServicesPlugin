namespace ObsServices.Core;

/// <summary>
/// The little astronomy an agent needs to speak the mount convention: the <c>ra</c> joint is the
/// hour angle in degrees west, so a mount that reports right ascension needs local sidereal time.
/// Same formulas as the service's <c>astro</c> module and the Python reference agent.
/// </summary>
public static class Astro {
    /// <summary>Julian date of a unix time.</summary>
    public static double JulianDate(double unixSeconds) => unixSeconds / 86400.0 + 2440587.5;

    /// <summary>Greenwich mean sidereal time in degrees, [0, 360).</summary>
    public static double GmstDeg(double julianDate) {
        var d = julianDate - 2451545.0;
        var t = d / 36525.0;
        var g = 280.46061837 + 360.98564736629 * d + 0.000387933 * t * t - t * t * t / 38710000.0;
        return Wrap360(g);
    }

    /// <summary>Local sidereal time in degrees, [0, 360), for an east-positive longitude.</summary>
    public static double LstDeg(double unixSeconds, double lonEastDeg) => Wrap360(GmstDeg(JulianDate(unixSeconds)) + lonEastDeg);

    /// <summary>Hour angle in degrees in (-180, 180]: positive west of the meridian.</summary>
    public static double HourAngleDeg(double lstDeg, double raDeg) {
        var ha = Wrap360(lstDeg - raDeg);
        return ha > 180.0 ? ha - 360.0 : ha;
    }

    /// <summary>Right ascension in degrees, [0, 360), from an hour angle.</summary>
    public static double RaDeg(double lstDeg, double hourAngleDeg) => Wrap360(lstDeg - hourAngleDeg);

    /// <summary>Altitude and azimuth in degrees; azimuth from north through east.</summary>
    public static (double AltDeg, double AzDeg) AltAz(double hourAngleDeg, double decDeg, double latDeg) {
        var (ha, dec, lat) = (ToRad(hourAngleDeg), ToRad(decDeg), ToRad(latDeg));
        var sinAlt = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(ha);
        var alt = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0));
        var y = -Math.Sin(ha) * Math.Cos(dec);
        var x = Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Sin(lat) * Math.Cos(ha);
        var az = Wrap360(ToDeg(Math.Atan2(y, x)));
        return (ToDeg(alt), az);
    }

    /// <summary>Hours to degrees (right ascension is often reported in hours).</summary>
    public static double HoursToDeg(double hours) => hours * 15.0;

    /// <summary>
    /// Axis angles of a German equatorial for a sky pose. The normal pose (tube east of the pier,
    /// the library convention) is (ha, dec); the flipped pose turns both axes through 180° and
    /// carries the tube west. Same rule as the service's virtual agent.
    /// </summary>
    public static (double RaAxisDeg, double DecAxisDeg) GemAxes(double hourAngleDeg, double decDeg, bool tubeWest) =>
        tubeWest ? (Wrap180(hourAngleDeg + 180.0), Wrap180(180.0 - decDeg)) : (Wrap180(hourAngleDeg), decDeg);

    /// <summary>Sky pose and side from axis angles: a dec axis beyond ±90° is the flipped pose.</summary>
    public static (double HourAngleDeg, double DecDeg, bool TubeWest) GemSky(double raAxisDeg, double decAxisDeg) {
        if (decAxisDeg > 90.0) {
            return (Wrap180(raAxisDeg - 180.0), 180.0 - decAxisDeg, true);
        }

        if (decAxisDeg < -90.0) {
            return (Wrap180(raAxisDeg - 180.0), -180.0 - decAxisDeg, true);
        }

        return (raAxisDeg, decAxisDeg, false);
    }

    /// <summary>Into (-180, 180].</summary>
    public static double Wrap180(double deg) {
        var w = Wrap360(deg);
        return w > 180.0 ? w - 360.0 : w;
    }

    /// <summary>Euclidean remainder into [0, 360).</summary>
    public static double Wrap360(double deg) {
        var r = deg % 360.0;
        return r < 0 ? r + 360.0 : r;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}
