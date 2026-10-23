using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class HarvestMinigame : ActivityMinigame
    {
        readonly int[] _plants = new int[9];
        int _remaining, _mistakes;
        protected override string Key => "Harvest";
        internal HarvestMinigame() { Restart(); }
        protected override void ResetRound()
        {
            for (int i = 0; i < _plants.Length; i++) _plants[i] = Random.Next(2, 4);
            _remaining = 4;
            _mistakes = 0;
            for (int i = 0; i < 4; i++)
            {
                int index;
                do { index = Random.Next(_plants.Length); } while (_plants[index] == 1);
                _plants[index] = 1;
            }
        }
        protected override void DrawBoard(Rect board)
        {
            float width = (board.width - 32) / 3, height = (board.height - 20) / 3;
            for (int i = 0; i < 9; i++)
            {
                var tile = new Rect(board.x + 16 + i % 3 * width, board.y + 10 + i / 3 * height, width - 8, height - 8);
                Widgets.DrawBoxSolid(tile, new Color(.13f, .12f, .085f));
                if (_plants[i] == 0) continue;
                var tint = _plants[i] == 1 ? Accent : _plants[i] == 2 ? new Color(.3f, .55f, .32f) : new Color(.55f, .28f, .3f);
                Widgets.DrawBoxSolid(new Rect(tile.center.x - 2, tile.y + 6, 4, tile.height - 12), new Color(.27f, .46f, .25f));
                for (int leaf = 0; leaf < 3; leaf++)
                    Widgets.DrawBoxSolid(new Rect(tile.center.x - 14 + leaf % 2 * 12, tile.y + 10 + leaf * 9, 16, 7), tint);
                if (_plants[i] == 3)
                    Stroke(new Vector2(tile.center.x - 16, tile.y + 8), new Vector2(tile.center.x + 16, tile.yMax - 8), new Color(.12f, .07f, .08f), 4);
                if (!CanPlay || !GUI.enabled || !Widgets.ButtonInvisible(tile)) continue;
                if (_plants[i] != 1) { _mistakes++; Mistake(); continue; }
                _plants[i] = 0;
                if (--_remaining == 0) { Complete(_mistakes == 0 ? 2 : 1); break; }
            }
        }
    }
}
