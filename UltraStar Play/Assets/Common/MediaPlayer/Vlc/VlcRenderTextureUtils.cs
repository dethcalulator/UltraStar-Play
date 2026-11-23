using UnityEngine;

public static class VlcRenderTextureUtils
{
    /**
     * Draws the source texture into the target render texture, keeping the aspect ratio of the source.
     * This mimics the aspect ratio handling of Unity's VideoPlayer.
     */
    public static void DrawWithAspectRatio(Texture source, RenderTexture target, ESongBackgroundScaleMode scaleMode)
    {
        if (source == null
            || target == null
            || source.width <= 0
            || source.height <= 0)
        {
            return;
        }

        float sourceAspectRatio = (float)source.width / source.height;
        float targetAspectRatio = (float)target.width / target.height;
        bool fitWidth = scaleMode == ESongBackgroundScaleMode.FitOutside
            ? sourceAspectRatio < targetAspectRatio
            : sourceAspectRatio > targetAspectRatio;

        float width = fitWidth
            ? target.width
            : target.height * sourceAspectRatio;
        float height = fitWidth
            ? target.width / sourceAspectRatio
            : target.height;
        Rect rect = new Rect((target.width - width) / 2, (target.height - height) / 2, width, height);

        RenderTexture oldActiveRenderTexture = RenderTexture.active;
        RenderTexture.active = target;
        GL.Clear(true, true, Color.black);
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, target.width, target.height, 0);
        Graphics.DrawTexture(rect, source);
        GL.PopMatrix();
        RenderTexture.active = oldActiveRenderTexture;
    }
}
