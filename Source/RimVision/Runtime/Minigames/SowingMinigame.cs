using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class SowingMinigame : ActivityMinigame
    {
        int _lane, _planted;
        bool _stone;
        float _position;
        protected override string Key => "Sowing";
        internal SowingMinigame() { Restart(); }
        protected override void ResetRound() { _planted = 0; Next(); }
        void Next()
        {
            _lane = Random.Next(3);
            _stone = Random.Next(4) == 0;
            _position = -.05f;
        }
        void Plant(int lane)
        {
            if (!CanPlay) return;
            if (_stone || lane != _lane || Mathf.Abs(_position - .72f) > .13f) { Mistake(true); return; }
            if (++_planted == 4) Complete();
            else Next();
        }
        protected override void Tick(float delta)
        {
            _position += delta * .72f;
            if (Input.GetKeyDown(KeyCode.Alpha1)) Plant(0);
            else if (Input.GetKeyDown(KeyCode.Alpha2)) Plant(1);
            else if (Input.GetKeyDown(KeyCode.Alpha3)) Plant(2);
            else if (_position > 1.03f)
            {
                if (_stone) Next();
                else Mistake(true);
            }
        }
        protected override void DrawBoard(Rect board)
        {
            float laneWidth = (board.width - 32) / 3, travel = board.height - 62;
            for (int i = 0; i < 3; i++)
            {
                float x = board.x + 16 + i * laneWidth;
                Widgets.DrawBoxSolid(new Rect(x + 5, board.y + 8, laneWidth - 10, travel), new Color(.2f, .145f, .09f));
                for (int j = 0; j < 7; j++)
                    Widgets.DrawBoxSolid(new Rect(x + 12, board.y + 12 + j * travel / 7, laneWidth - 24, 2), new Color(.12f, .09f, .06f));
                Widgets.DrawBoxSolid(new Rect(x + 5, board.y + 8 + travel * .72f - 2, laneWidth - 10, 4), Accent);
                if (i == _lane && _position >= 0 && _position <= 1)
                {
                    float y = board.y + 8 + _position * travel;
                    Widgets.DrawBoxSolid(new Rect(x + laneWidth / 2 - 13, y - 8, 26, 16),
                        _stone ? new Color(.5f, .55f, .56f) : new Color(.06f, .045f, .025f));
                }
                if (Button(new Rect(x + 4, board.yMax - 39, laneWidth - 8, 29), (i + 1).ToString())) Plant(i);
            }
            Bar(new Rect(board.x + 16, board.yMax - 7, board.width - 32, 3), _planted / 4f, Accent);
        }
    }
}
