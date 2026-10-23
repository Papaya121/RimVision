using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class WoodcuttingMinigame : WorkMinigame
    {
        const float StrikeLine = .78f, HitRadius = .12f, PerfectRadius = .035f;
        readonly System.Random _random = new System.Random();
        int _score;
        bool _left, _paused = true, _lastLeft, _swing;
        float _position, _cooldown, _feedbackTime;
        string _feedback;
        Color _feedbackColor;
        public override string Title => "Rim3D_MinigameWoodcutting".Translate().ToString();
        public override int PointsPerReward => 1;
        public override string Instructions => "Rim3D_WoodcuttingHint".Translate().ToString();
        public override int Score => _score;

        internal WoodcuttingMinigame() { Restart(); }

        public override void Restart()
        {
            _score = 0;
            _cooldown = .4f;
            _feedbackTime = 0;
            _feedback = null;
            _paused = true;
            _swing = false;
            NextMark();
        }

        void NextMark()
        {
            _left = _random.Next(2) == 0;
            _position = -.05f;
        }

        public override void Update(bool paused)
        {
            _paused = paused;
            if (paused) return;
            float delta = Mathf.Min(Time.unscaledDeltaTime, .05f);
            _feedbackTime = Mathf.Max(0, _feedbackTime - delta);
            _cooldown = Mathf.Max(0, _cooldown - delta);
            if (_cooldown > 0) return;
            _position += delta * .58f;
            bool left = Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow);
            bool right = Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow);
            if (left || right) Strike(left, left && right);
            else if (_position > 1.05f) FinishStrike("Rim3D_WoodcuttingMiss", new Color(.9f, .45f, .35f), false);
        }

        void Strike(bool left, bool both = false)
        {
            if (_paused || _cooldown > 0) return;
            _lastLeft = left;
            float distance = Mathf.Abs(_position - StrikeLine);
            if (both || left != _left || distance > HitRadius)
            {
                FinishStrike("Rim3D_WoodcuttingMiss", new Color(.9f, .45f, .35f));
                return;
            }
            bool perfect = distance <= PerfectRadius;
            _score += perfect ? 2 : 1;
            FinishStrike(perfect ? "Rim3D_WoodcuttingPerfect" : "Rim3D_WoodcuttingHit",
                perfect ? new Color(1f, .8f, .4f) : new Color(.5f, .85f, .65f));
        }

        void FinishStrike(string feedback, Color color, bool swing = true)
        {
            _swing = swing;
            _feedback = feedback.Translate().ToString();
            _feedbackColor = color;
            _feedbackTime = 1.1f;
            _cooldown = .25f;
            NextMark();
        }

        void DrawForest(Rect board)
        {
            Widgets.DrawBoxSolid(board, new Color(.04f, .075f, .07f));
            for (int i = 0; i < 11; i++)
            {
                float x = board.x + (i + .3f) * board.width / 11;
                float top = board.y + (i * 13 % 37);
                Widgets.DrawBoxSolid(new Rect(x, top, 7 + i % 3, board.yMax - top), new Color(.065f, .12f, .105f));
                Widgets.DrawBoxSolid(new Rect(x - 9, top + 17, 27, 5), new Color(.065f, .12f, .105f));
            }
            float center = board.center.x;
            var trunk = new Rect(center - 46, board.y, 92, board.height);
            Widgets.DrawBoxSolid(new Rect(trunk.x - 5, trunk.y, trunk.width + 10, trunk.height), new Color(.1f, .075f, .055f));
            Widgets.DrawBoxSolid(trunk, new Color(.31f, .21f, .13f));
            for (int i = 0; i < 15; i++)
            {
                float x = trunk.x + 3 + i * 6;
                var shade = i % 3 == 0 ? new Color(.43f, .3f, .18f) : new Color(.2f, .135f, .085f);
                for (int j = 0; j < 3; j++)
                {
                    float y = board.y + j * board.height / 3;
                    Widgets.DrawBoxSolid(new Rect(x + (i + j) % 3, y + 3, 2 + i % 3, board.height / 3 - 6), shade);
                }
            }
            float strikeY = board.y + board.height * StrikeLine;
            Widgets.DrawBoxSolid(new Rect(trunk.x - 27, strikeY - board.height * HitRadius, trunk.width + 54,
                board.height * HitRadius * 2), new Color(.4f, .75f, .55f, .14f));
            Widgets.DrawBoxSolid(new Rect(trunk.x - 27, strikeY - 1, trunk.width + 54, 2), new Color(.85f, .73f, .43f));
            if (_position >= 0 && _position <= 1)
            {
                float x = center + (_left ? -34 : 34);
                float y = board.y + _position * board.height;
                Widgets.DrawBoxSolid(new Rect(x - 13, y - 10, 26, 20), new Color(.08f, .12f, .1f));
                Widgets.DrawBoxSolid(new Rect(x - 11, y - 8, 22, 16), new Color(.95f, .73f, .36f));
                GUI.color = new Color(.12f, .1f, .07f);
                Widgets.Label(new Rect(x - 13, y - 13, 26, 26), _left ? "A" : "D");
                GUI.color = Color.white;
            }
            if (_swing && _cooldown > 0 && _feedbackTime > 0)
            {
                float travel = 1f - _cooldown / .25f;
                float side = _lastLeft ? -1 : 1;
                float axeX = center + side * (63 - travel * 25);
                Widgets.DrawBoxSolid(new Rect(axeX + side * 8 - 3, strikeY - 4, 6, 42), new Color(.67f, .45f, .24f));
                Widgets.DrawBoxSolid(new Rect(axeX - 15, strikeY - 12, 30, 20), new Color(.7f, .76f, .77f));
                Widgets.DrawBoxSolid(new Rect(axeX - 15, strikeY - 12, 30, 3), new Color(.93f, .95f, .92f));
                for (int i = 0; i < 7; i++)
                {
                    float x = center + side * (45 + travel * (14 + i * 4));
                    float y = strikeY - 13 + i * 4 - Mathf.Sin(travel * Mathf.PI) * (8 + i * 3);
                    Widgets.DrawBoxSolid(new Rect(x, y, 4, 2), new Color(.85f, .62f, .34f, 1f - travel));
                }
            }
        }

        void DrawButton(Rect rect, bool left)
        {
            var edge = GUI.enabled && rect.Contains(Event.current.mousePosition) ?
                new Color(.95f, .73f, .36f) : new Color(.35f, .4f, .33f);
            Widgets.DrawBoxSolid(rect, edge);
            Widgets.DrawBoxSolid(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2), new Color(.08f, .12f, .1f));
            Widgets.Label(rect, (left ? "Rim3D_WoodcuttingLeft" : "Rim3D_WoodcuttingRight").Translate());
            if (Widgets.ButtonInvisible(rect)) Strike(left);
        }

        public override void Draw(Rect rect)
        {
            var font = Text.Font;
            var anchor = Text.Anchor;
            var color = GUI.color;
            bool enabled = GUI.enabled;
            try
            {
                GUI.color = Color.white;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperLeft;
                Widgets.Label(new Rect(rect.x, rect.y, rect.width, 44), Instructions);
                var board = new Rect(rect.x, rect.y + 48, rect.width, Mathf.Max(60, rect.height - 168));
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleCenter;
                DrawForest(board);
                GUI.color = _feedbackTime > 0 ? _feedbackColor : Color.white;
                Widgets.Label(new Rect(rect.x, board.yMax + 5, rect.width, 28), _feedbackTime > 0 ? _feedback :
                    "Rim3D_WoodcuttingReady".Translate().ToString());
                GUI.color = Color.white;
                GUI.enabled = enabled && !_paused && _cooldown <= 0;
                float width = (rect.width - 24) / 2;
                DrawButton(new Rect(rect.x + 8, board.yMax + 39, width, 34), true);
                DrawButton(new Rect(rect.x + 16 + width, board.yMax + 39, width, 34), false);
                GUI.enabled = enabled;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rect.x, board.yMax + 80, rect.width, 32), "Rim3D_WoodcuttingReward".Translate(
                    PointsPerReward, WorkMinigames.WorkFractionPerReward * 100));
            }
            finally
            {
                GUI.enabled = enabled;
                GUI.color = color;
                Text.Font = font;
                Text.Anchor = anchor;
            }
        }
    }
}
