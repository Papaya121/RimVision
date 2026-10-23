using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class BuildingMinigame : ActivityMinigame
    {
        int _placed, _precise;
        float _phase;
        protected override string Key => "Building";
        internal BuildingMinigame() { Restart(); }
        protected override void ResetRound()
        {
            _placed = _precise = 0;
            _phase = (float)Random.NextDouble() * 2;
        }
        protected override void Tick(float delta)
        {
            _phase = (_phase + delta * (.65f + _placed * .13f)) % 2;
            if (Input.GetKeyDown(KeyCode.Space)) Place();
        }
        void Place()
        {
            if (!CanPlay) return;
            float error = Mathf.Abs(Mathf.PingPong(_phase, 1) - .5f);
            if (error > .12f) { Mistake(true); return; }
            if (error <= .035f) _precise++;
            _placed++;
            _phase = 0;
            if (_placed == 3) Complete(_precise == 3 ? 2 : 1);
        }
        protected override void DrawBoard(Rect board)
        {
            for (int i = 0; i < 9; i++)
                Widgets.DrawBoxSolid(new Rect(board.x + i * board.width / 8, board.y, 1, board.height - 44), new Color(.09f, .16f, .2f));
            float center = board.center.x;
            for (int i = 0; i < 3; i++)
            {
                float y = board.y + 25 + i * 42;
                Widgets.DrawBoxSolid(new Rect(center - 72, y - 3, 144, 30), new Color(.22f, .34f, .38f));
                Widgets.DrawBoxSolid(new Rect(center - 68, y, 136, 24), new Color(.055f, .1f, .13f));
                if (i > _placed) continue;
                float x = i < _placed ? center : center + (Mathf.PingPong(_phase, 1) - .5f) * 140;
                Widgets.DrawBoxSolid(new Rect(x - 50, y + 2, 100, 20), i < _placed ? Accent : new Color(.4f, .7f, .72f));
                Widgets.DrawBoxSolid(new Rect(x - 45, y + 4, 90, 2), new Color(.8f, .86f, .78f));
            }
            if (Button(new Rect(board.x + 12, board.yMax - 40, board.width - 24, 30), "Rim3D_ActivityPlace".Translate())) Place();
        }
    }
}
