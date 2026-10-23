using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class BandagingMinigame : ActivityMinigame
    {
        int _laid, _precise;
        bool _placing;
        float _end;
        protected override string Key => "Bandaging";
        internal BandagingMinigame() { Restart(); }
        protected override void ResetRound() { _laid = _precise = 0; CancelGesture(); }
        protected override void CancelGesture() { _placing = false; _end = .14f; }
        protected override void DrawBoard(Rect board)
        {
            var skin = new Rect(board.x + 20, board.y + 14, board.width - 40, board.height - 32);
            Widgets.DrawBoxSolid(skin, new Color(.32f, .22f, .2f));
            Stroke(Point(skin, new Vector2(.37f, .12f)), Point(skin, new Vector2(.6f, .86f)), new Color(.55f, .22f, .22f), 14);
            for (int i = 0; i < 4; i++)
            {
                float y = skin.y + (.2f + i * .18f) * skin.height;
                Widgets.DrawBoxSolid(new Rect(skin.x + skin.width * .13f, y - 9, skin.width * .76f, 18), new Color(.42f, .38f, .32f));
                if (i < _laid || i == _laid && _placing)
                {
                    float end = i < _laid ? .86f : _end;
                    Widgets.DrawBoxSolid(new Rect(skin.x + skin.width * .14f, y - 8, skin.width * (end - .14f), 16), new Color(.83f, .81f, .7f));
                    Widgets.DrawBoxSolid(new Rect(skin.x + skin.width * .14f, y - 4, skin.width * (end - .14f), 2), new Color(.93f, .92f, .85f));
                }
                if (i == _laid)
                {
                    Widgets.DrawBoxSolid(new Rect(skin.x + skin.width * .1f, y - 10, skin.width * .1f, 20), new Color(.35f, .7f, .6f));
                    Widgets.DrawBoxSolid(new Rect(skin.x + skin.width * .8f, y - 10, skin.width * .14f, 20), Accent);
                }
            }
            var e = Event.current;
            if (!CanPlay || !GUI.enabled) return;
            float x = (e.mousePosition.x - skin.x) / skin.width;
            float row = skin.y + (.2f + _laid * .18f) * skin.height;
            if (e.type == EventType.MouseDown && e.button == 0 && x >= .1f && x <= .2f && Mathf.Abs(e.mousePosition.y - row) <= 14)
            {
                _placing = true;
                _end = .14f;
                e.Use();
            }
            else if (_placing && e.type == EventType.MouseDrag && e.button == 0)
            {
                if (Mathf.Abs(e.mousePosition.y - row) > 18) { CancelGesture(); Mistake(); }
                else _end = Mathf.Clamp(x, .14f, 1f);
                e.Use();
            }
            else if (_placing && e.type == EventType.MouseUp && e.button == 0)
            {
                _placing = false;
                if (x < .8f || x > .94f || Mathf.Abs(e.mousePosition.y - row) > 18) { CancelGesture(); Mistake(); }
                else
                {
                    if (x >= .84f && x <= .89f) _precise++;
                    if (++_laid == 4) Complete(_precise == 4 ? 2 : 1);
                }
                e.Use();
            }
            else if (_placing && e.type == EventType.Repaint && !Input.GetMouseButton(0)) CancelGesture();
        }
    }
}
