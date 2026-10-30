using Ryujinx.Common.Configuration;
using System;

namespace Ryujinx.Library
{
    internal sealed record LiveGraphicsSettings(float Scale, float Anisotropy, AntiAliasing AntiAliasing,
        ScalingFilter Filter, int FilterLevel, float MetalFxScale, AspectRatio AspectRatio, float Volume, bool AsyncShaders)
    {
        public static LiveGraphicsSettings Create(float scale, float anisotropy, int aa, int filter, int level,
            float metalFxScale, int aspect, float volume, bool asyncShaders)
        {
            if (!float.IsFinite(scale) || scale < 0.1f || scale > 16 ||
                !float.IsFinite(anisotropy) || anisotropy < -1 || anisotropy > 16 ||
                !float.IsFinite(metalFxScale) || metalFxScale < 1 || metalFxScale > 4 ||
                !float.IsFinite(volume) || volume < 0 || volume > 1 ||
                !Enum.IsDefined((AntiAliasing)aa) || !Enum.IsDefined((ScalingFilter)filter) ||
                !Enum.IsDefined((AspectRatio)aspect)) return null;

            return new(scale, anisotropy, (AntiAliasing)aa, (ScalingFilter)filter, Math.Clamp(level, 0, 100),
                metalFxScale, (AspectRatio)aspect, volume, asyncShaders);
        }
    }
}
