using System;

namespace TFM.Core
{
    /// <summary>Plain 3-float vector (kept free of UnityEngine so the core library is engine-independent).</summary>
    [Serializable]
    public struct Vec3
    {
        public float X, Y, Z;

        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.#####}, {1:0.#####}, {2:0.#####})", X, Y, Z);
        }
    }

    /// <summary>
    /// Plain quaternion stored exactly as it appears in the file (x, y, z, w).
    /// Euler helpers use Unity's convention (same as Quaternion.Euler / Quaternion.eulerAngles):
    /// rotation order Z, then X, then Y, angles in degrees.
    /// </summary>
    [Serializable]
    public struct Quat
    {
        public float X, Y, Z, W;

        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public static Quat Identity { get { return new Quat(0, 0, 0, 1); } }

        public float Length { get { return (float)Math.Sqrt((double)X * X + (double)Y * Y + (double)Z * Z + (double)W * W); } }

        public Quat Normalized()
        {
            float len = Length;
            if (len < 1e-12f) return Identity;
            return new Quat(X / len, Y / len, Z / len, W / len);
        }

        /// <summary>Unity-style euler angles in degrees, each in [0, 360).</summary>
        public Vec3 ToEulerDegrees()
        {
            Quat q = Normalized();
            double x = q.X, y = q.Y, z = q.Z, w = q.W;

            // Matches Unity's ZXY decomposition.
            double sinX = 2.0 * (w * x - y * z);
            double ex, ey, ez;
            if (Math.Abs(sinX) >= 0.9999995)
            {
                // Gimbal lock: pitch +/-90, fold roll into yaw.
                ex = Math.PI / 2.0 * Math.Sign(sinX);
                ey = 2.0 * Math.Atan2(y, w);
                ez = 0.0;
            }
            else
            {
                ex = Math.Asin(sinX);
                ey = Math.Atan2(2.0 * (w * y + x * z), 1.0 - 2.0 * (x * x + y * y));
                ez = Math.Atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (x * x + z * z));
            }
            return new Vec3(Wrap360(ex), Wrap360(ey), Wrap360(ez));
        }

        /// <summary>Equivalent of UnityEngine.Quaternion.Euler(x, y, z).</summary>
        public static Quat FromEulerDegrees(Vec3 e)
        {
            double d2r = Math.PI / 180.0;
            double hx = e.X * d2r * 0.5, hy = e.Y * d2r * 0.5, hz = e.Z * d2r * 0.5;
            double sx = Math.Sin(hx), cx = Math.Cos(hx);
            double sy = Math.Sin(hy), cy = Math.Cos(hy);
            double sz = Math.Sin(hz), cz = Math.Cos(hz);

            // q = qY * qX * qZ
            double w = cy * cx * cz + sy * sx * sz;
            double x = cy * sx * cz + sy * cx * sz;
            double y = sy * cx * cz - cy * sx * sz;
            double z = cy * cx * sz - sy * sx * cz;
            return new Quat((float)x, (float)y, (float)z, (float)w);
        }

        static float Wrap360(double rad)
        {
            double deg = rad * 180.0 / Math.PI;
            deg %= 360.0;
            if (deg < 0) deg += 360.0;
            if (deg >= 359.99995) deg = 0.0;
            return (float)deg;
        }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.#####}, {1:0.#####}, {2:0.#####}, {3:0.#####})", X, Y, Z, W);
        }
    }
}
