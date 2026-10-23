using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class SmoothingMinigame : ActivityMinigame
    {
        const float Target = .35f;
        readonly float[] _heights = new float[8];
        int _lastFrame = -1;
        bool _polishing;
        protected override string Key => "Smoothing";
        internal SmoothingMinigame() { Restart(); }
        protected override void ResetRound()
        {
            _polishing = false;
            for (int i = 0; i < _heights.Length; i++) _heights[i] = .5f + (float)Random.NextDouble() * .3f;
        }
        protected override void CancelGesture() { _polishing = false; }
        protected override void DrawBoard(Rect board)
        {
            var surface = new Rect(board.x + 16, board.y + 18, board.width - 32, board.height - 46);
            float width = surface.width / _heights.Length;
            float targetY = surface.yMax - surface.height * Target;
            Widgets.DrawBoxSolid(new Rect(surface.x, targetY, surface.width, surface.height * Target), new Color(.2f, .28f, .3f));
            int complete = 0;
            for (int i = 0; i < _heights.Length; i++)
            {
                float height = _heights[i] * surface.height;
                Widgets.DrawBoxSolid(new Rect(surface.x + i * width + 2, surface.yMax - height, width - 4, height),
                    _heights[i] <= Target ? Accent : new Color(.4f, .5f, .53f));
                if (_heights[i] <= Target) complete++;
            }
            Widgets.DrawBoxSolid(new Rect(surface.x, targetY - 1, surface.width, 2), new Color(.5f, .85f, .7f));
            Bar(new Rect(board.x + 16, board.yMax - 12, board.width - 32, 5), complete / 8f, Accent);
            var e = Event.current;
            if (!CanPlay || !GUI.enabled) return;
            if (e.type == EventType.Repaint && !Input.GetMouseButton(0)) _polishing = false;
            if (e.type == EventType.MouseDown && e.button == 0 && surface.Contains(e.mousePosition))
            {
                _polishing = true;
                e.Use();
            }
            else if (_polishing && e.type == EventType.MouseDrag && e.button == 0) e.Use();
            else if (_polishing && e.type == EventType.MouseUp && e.button == 0) { _polishing = false; e.Use(); }
            if (!surface.Contains(e.mousePosition)) return;
            Widgets.DrawBoxSolid(new Rect(e.mousePosition.x - 17, e.mousePosition.y - 4, 34, 8), new Color(.8f, .72f, .5f));
            if (!_polishing || !Input.GetMouseButton(0) || e.type != EventType.Repaint || _lastFrame == Time.frameCount) return;
            _lastFrame = Time.frameCount;
            int column = Mathf.Clamp((int)((e.mousePosition.x - surface.x) / width), 0, _heights.Length - 1);
            float top = surface.yMax - _heights[column] * surface.height;
            if (Mathf.Abs(e.mousePosition.y - top) > 18) return;
            _heights[column] = Mathf.Max(Target, _heights[column] - Mathf.Min(Time.unscaledDeltaTime, .05f) * .8f);
            bool finished = true;
            for (int i = 0; i < _heights.Length; i++) if (_heights[i] > Target) { finished = false; break; }
            if (finished) Complete();
        }
    }
}
