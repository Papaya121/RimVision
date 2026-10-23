using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class DismantlingMinigame : ActivityMinigame
    {
        int _removed;
        protected override string Key => "Dismantling";
        internal DismantlingMinigame() { Restart(); }
        protected override void ResetRound() { _removed = 0; }
        void Remove(int part)
        {
            if (!CanPlay || (_removed & (1 << part)) != 0) return;
            if (part >= 4 && (_removed & 15) != 15 || part == 6 && (_removed & 63) != 63) { Mistake(); return; }
            _removed |= 1 << part;
            if (_removed == 127) Complete();
        }
        protected override void DrawBoard(Rect board)
        {
            var frame = new Rect(board.x + 24, board.y + 16, board.width - 48, board.height - 68);
            Widgets.DrawBoxSolid(frame, new Color(.36f, .4f, .42f));
            Widgets.DrawBoxSolid(new Rect(frame.x + 8, frame.y + 8, frame.width - 16, frame.height - 16), new Color(.07f, .1f, .12f));
            for (int i = 0; i < 2; i++)
            {
                if ((_removed & (1 << (i + 4))) != 0) continue;
                var panel = new Rect(frame.x + 31 + i * (frame.width - 62) / 2, frame.y + 24, (frame.width - 66) / 2, frame.height - 48);
                if (Button(panel, "Rim3D_ActivityPanel".Translate())) Remove(i + 4);
            }
            for (int i = 0; i < 4; i++)
            {
                if ((_removed & (1 << i)) != 0) continue;
                var bolt = new Rect(i % 2 == 0 ? frame.x + 3 : frame.xMax - 25, i < 2 ? frame.y + 3 : frame.yMax - 25, 22, 22);
                if (Button(bolt, "+")) Remove(i);
            }
            if (Button(new Rect(board.x + 24, board.yMax - 41, board.width - 48, 30), "Rim3D_ActivityFrame".Translate())) Remove(6);
        }
    }
}
