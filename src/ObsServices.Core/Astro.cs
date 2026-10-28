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

    /// <summary>Euclidean remainder into [0, 360).</summary>
    public static double Wrap360(double deg) {
        var r = deg % 360.0;
        return r < 0 ? r + 360.0 : r;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}
