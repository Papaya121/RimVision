using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class ChessMinigame : ActivityMinigame
    {
        int _knight, _queen, _rook, _whiteKing, _blackKing, _mistakes;
        bool _selected;
        protected override string Key => "Chess";
        public override Vector2 WindowSize => new Vector2(520, 600);
        protected override string RewardHint => "Rim3D_RecreationReward".Translate(WorkMinigames.WorkFractionPerReward * 100).ToString();
        internal ChessMinigame() { Restart(); }

        static bool KnightAttacks(int a, int b)
        {
            int x = Mathf.Abs(a % 8 - b % 8), y = Mathf.Abs(a / 8 - b / 8);
            return x * y == 2;
        }

        static int KingDistance(int a, int b) => Mathf.Max(Mathf.Abs(a % 8 - b % 8), Mathf.Abs(a / 8 - b / 8));

        static bool RookAttacks(int a, int b) => a % 8 == b % 8 || a / 8 == b / 8;

        static bool QueenAttacks(int a, int b) => RookAttacks(a, b) || Mathf.Abs(a % 8 - b % 8) == Mathf.Abs(a / 8 - b / 8);

        protected override void ResetRound()
        {
            _selected = false;
            _mistakes = 0;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                int fork = Random.Next(2, 6) + Random.Next(2, 6) * 8;
                _knight = KnightSquare(fork, -1, -1);
                _queen = KnightSquare(fork, _knight, -1);
                _rook = KnightSquare(fork, _knight, _queen);
                _whiteKing = -1;
                int start = Random.Next(64);
                for (int i = 0; i < 64; i++)
                {
                    int square = (start + i) % 64;
                    if (square == fork || square == _knight || QueenAttacks(_queen, square) || RookAttacks(_rook, square)) continue;
                    _whiteKing = square;
                    break;
                }
                if (_whiteKing < 0) continue;
                _blackKing = -1;
                start = Random.Next(64);
                for (int i = 0; i < 64; i++)
                {
                    int square = (start + i) % 64;
                    if (square == _queen || square == _rook || KingDistance(square, _whiteKing) <= 1 ||
                        KingDistance(square, fork) <= 1 || KingDistance(square, _knight) <= 1 ||
                        KnightAttacks(_knight, square) || KnightAttacks(fork, square)) continue;
                    _blackKing = square;
                    return;
                }
            }
            _knight = 17; _queen = 37; _rook = 12; _whiteKing = 56; _blackKing = 63;
        }

        int KnightSquare(int origin, int excludedA, int excludedB)
        {
            int start = Random.Next(64);
            for (int i = 0; i < 64; i++)
            {
                int square = (start + i) % 64;
                if (square != excludedA && square != excludedB && KnightAttacks(origin, square)) return square;
            }
            return -1;
        }

        bool LegalMove(int square) => square != _whiteKing && square != _blackKing &&
            KnightAttacks(_knight, square) && KingDistance(square, _blackKing) > 1;

        void Choose(int square)
        {
            if (!CanPlay) return;
            if (square == _knight) { _selected = true; return; }
            if (!_selected) return;
            if (LegalMove(square) && square != _queen && square != _rook &&
                KnightAttacks(square, _queen) && KnightAttacks(square, _rook))
            {
                Complete(_mistakes == 0 ? 2 : 1);
                return;
            }
            _mistakes++;
            _selected = false;
            Mistake();
        }

        void DrawPiece(Rect square, string piece, bool white)
        {
            var center = square.center;
            float size = square.width;
            var tint = white ? new Color(.93f, .9f, .77f) : new Color(.13f, .18f, .2f);
            Widgets.DrawBoxSolid(new Rect(center.x - size * .31f, square.yMax - size * .22f, size * .62f, size * .12f), tint);
            Widgets.DrawBoxSolid(new Rect(center.x - size * .23f, square.y + size * .12f, size * .46f, size * .62f), tint);
            var color = GUI.color;
            GUI.color = white ? new Color(.12f, .17f, .18f) : new Color(.92f, .85f, .62f);
            Widgets.Label(new Rect(square.x, square.y, square.width, square.height - size * .12f), piece);
            GUI.color = color;
        }

        protected override void DrawBoard(Rect board)
        {
            float side = Mathf.Min(board.width - 44, board.height - 12), cell = side / 8;
            var grid = new Rect(board.center.x - side / 2, board.y + 4, side, side);
            var font = Text.Font;
            Text.Font = GameFont.Small;
            for (int i = 0; i < 64; i++)
            {
                var tile = new Rect(grid.x + i % 8 * cell, grid.y + (7 - i / 8) * cell, cell, cell);
                var tint = (i % 8 + i / 8) % 2 == 0 ? new Color(.29f, .4f, .39f) : new Color(.7f, .71f, .57f);
                if (i == _knight && _selected) tint = Accent;
                Widgets.DrawBoxSolid(tile, tint);
                if (i == _knight) DrawPiece(tile, "N", true);
                else if (i == _queen) DrawPiece(tile, "Q", false);
                else if (i == _rook) DrawPiece(tile, "R", false);
                else if (i == _whiteKing) DrawPiece(tile, "K", true);
                else if (i == _blackKing) DrawPiece(tile, "K", false);
                else if (_selected && LegalMove(i))
                    Widgets.DrawBoxSolid(new Rect(tile.center.x - 2, tile.center.y - 2, 4, 4), new Color(.92f, .7f, .3f));
                if (CanPlay && GUI.enabled && Widgets.ButtonInvisible(tile)) { Choose(i); break; }
            }
            Text.Font = font;
        }
    }
}
