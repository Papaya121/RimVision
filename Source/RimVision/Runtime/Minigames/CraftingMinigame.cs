using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class CraftingMinigame : ActivityMinigame
    {
        readonly Vector2[] _path = new Vector2[6];
        int _next, _mistakes;
        bool _tracing;
        protected override string Key => "Crafting";
        internal CraftingMinigame() { Restart(); }
        protected override void ResetRound()
        {
            for (int i = 0; i < _path.Length; i++) _path[i] = new Vector2(.12f + i * .15f, .2f + (float)Random.NextDouble() * .58f);
            _next = 1;
            _mistakes = 0;
            _tracing = false;
        }
        protected override void CancelGesture() { _tracing = false; _next = 1; }
        void Slip()
        {
            CancelGesture();
            _mistakes++;
            Mistake();
        }
        protected override void DrawBoard(Rect board)
        {
            for (int i = 1; i < _path.Length; i++)
                Stroke(Point(board, _path[i - 1]), Point(board, _path[i]), i < _next ? Accent : new Color(.25f, .4f, .45f), 9);
            for (int i = 0; i < _path.Length; i++)
            {
                var point = Point(board, _path[i]);
                Widgets.DrawBoxSolid(new Rect(point.x - 10, point.y - 10, 20, 20), i < _next ? Accent : new Color(.4f, .65f, .68f));
                Widgets.Label(new Rect(point.x - 10, point.y - 10, 20, 20), (i + 1).ToString());
            }
            Bar(new Rect(board.x + 16, board.yMax - 13, board.width - 32, 5), (_next - 1) / 5f, Accent);
            var e = Event.current;
            if (!CanPlay || !GUI.enabled) return;
            if (e.type == EventType.MouseDown && e.button == 0 && Vector2.Distance(e.mousePosition, Point(board, _path[0])) <= 18)
            {
                _tracing = true;
                _next = 1;
                e.Use();
            }
            else if (_tracing && e.type == EventType.MouseDrag && e.button == 0)
            {
                var start = Point(board, _path[_next - 1]);
                var end = Point(board, _path[_next]);
                var delta = end - start;
                float along = Mathf.Clamp01(Vector2.Dot(e.mousePosition - start, delta) / delta.sqrMagnitude);
                if (Vector2.Distance(e.mousePosition, start + delta * along) > 18) Slip();
                else if (Vector2.Distance(e.mousePosition, end) <= 15 && ++_next == _path.Length) Complete(_mistakes == 0 ? 2 : 1);
                e.Use();
            }
            else if (_tracing && e.type == EventType.MouseUp && e.button == 0) { Slip(); e.Use(); }
            else if (_tracing && e.type == EventType.Repaint && !Input.GetMouseButton(0)) CancelGesture();
        }
    }
}
