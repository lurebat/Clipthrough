#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.ImageEditor.Core.ImageEffects.Parameters;
using ShareX.ImageEditor.Presentation.Theming;
using SkiaSharp;

namespace ShareX.ImageEditor.Core.ImageEffects.Manipulations;

public sealed class ResizeImageEffect : ImageEffectBase
{
    public override string Id => "resize_image";
    public override string Name => "Resize image";
    public override ImageEffectCategory Category => ImageEffectCategory.Manipulations;
    public override string IconKey => LucideIcons.scaling;
    public override string Description => "Resizes the image.";
    public override IReadOnlyList<EffectParameter> Parameters =>
    [
        EffectParameters.IntNumeric<ResizeImageEffect>("width", "Width", 0, 10000, 0, (e, v) => e.Width = v),
        EffectParameters.IntNumeric<ResizeImageEffect>("height", "Height", 0, 10000, 0, (e, v) => e.Height = v),
        EffectParameters.Bool<ResizeImageEffect>("maintain_aspect_ratio", "Maintain aspect ratio", false, (e, v) => e.MaintainAspectRatio = v)
    ];

    public int Width { get; set; }
    public int Height { get; set; }
    public bool MaintainAspectRatio { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        int width = Width > 0 ? Width : source.Width;
        int height = Height > 0 ? Height : source.Height;

        if (width <= 0) width = source.Width;
        if (height <= 0) height = source.Height;

        if (MaintainAspectRatio)
        {
            double sourceAspect = (double)source.Width / source.Height;
            double targetAspect = (double)width / height;

            if (sourceAspect > targetAspect)
            {
                height = (int)Math.Round(width / sourceAspect);
            }
            else
            {
                width = (int)Math.Round(height * sourceAspect);
            }
        }

        SKImageInfo info = new SKImageInfo(width, height, source.ColorType, source.AlphaType, source.ColorSpace);
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.CatmullRom));
    }
}