using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class RepairMinigame : ActivityMinigame
    {
        readonly int[] _faults = new int[12];
        int _selected, _remaining, _mistakes;
        protected override string Key => "Repair";
        internal RepairMinigame() { Restart(); }
        protected override void ResetRound()
        {
            System.Array.Clear(_faults, 0, _faults.Length);
            _selected = -1;
            _remaining = 3;
            _mistakes = 0;
            for (int i = 0; i < 3; i++)
            {
                int index;
                do { index = Random.Next(_faults.Length); } while (_faults[index] != 0);
                _faults[index] = i + 1;
            }
        }
        void Apply(int tool)
        {
            if (!CanPlay || _selected < 0) return;
            if (_faults[_selected] != tool) { _mistakes++; Mistake(); return; }
            _faults[_selected] = 0;
            _selected = -1;
            if (--_remaining == 0) Complete(_mistakes == 0 ? 2 : 1);
        }
        protected override void DrawBoard(Rect board)
        {
            float w = (board.width - 24) / 4, h = (board.height - 74) / 3;
            for (int i = 0; i < 12; i++)
            {
                var tile = new Rect(board.x + 12 + i % 4 * w, board.y + 8 + i / 4 * h, w - 5, h - 5);
                Widgets.DrawBoxSolid(tile, i == _selected ? new Color(.25f, .35f, .36f) : new Color(.09f, .15f, .17f));
                var tint = _faults[i] == 0 ? new Color(.3f, .6f, .48f) : _faults[i] == 1 ? new Color(.9f, .45f, .35f) :
                    _faults[i] == 2 ? new Color(.9f, .72f, .35f) : new Color(.4f, .7f, .95f);
                Stroke(new Vector2(tile.x + 4, tile.center.y), new Vector2(tile.xMax - 4, tile.center.y), tint, 3);
                Widgets.DrawBoxSolid(new Rect(tile.center.x - 11, tile.center.y - 10, 22, 20), new Color(.055f, .09f, .1f));
                Widgets.Label(tile, _faults[i] == 0 ? "•" : _faults[i].ToString());
                if (CanPlay && GUI.enabled && Widgets.ButtonInvisible(tile)) _selected = i;
            }
            for (int i = 1; i <= 3; i++)
                if (Button(new Rect(board.x + 8 + (i - 1) * (board.width - 16) / 3, board.yMax - 42,
                    (board.width - 16) / 3 - 4, 32), ("Rim3D_ActivityTool" + i).Translate())) Apply(i);
        }
    }
}
