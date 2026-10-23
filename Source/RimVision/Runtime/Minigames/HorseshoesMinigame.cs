using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class HorseshoesMinigame : ActivityMinigame
    {
        int _stage;
        float _aimPhase, _powerPhase, _aim, _wind, _flight;
        Vector2 _landing;
        protected override string Key => "Horseshoes";
        protected override string RewardHint => "Rim3D_RecreationReward".Translate(WorkMinigames.WorkFractionPerReward * 100).ToString();
        internal HorseshoesMinigame() { Restart(); }

        protected override void ResetRound()
        {
            _stage = 0;
            _aimPhase = (float)Random.NextDouble() * 2;
            _powerPhase = _flight = 0;
            _wind = (float)Random.NextDouble() * .9f - .45f;
        }

        protected override void Tick(float delta)
        {
            if (_stage == 0) _aimPhase = (_aimPhase + delta * .7f) % 2;
            else if (_stage == 1) _powerPhase = (_powerPhase + delta * .85f) % 2;
            else if (_stage == 2)
            {
                _flight = Mathf.Min(1, _flight + delta * 1.2f);
                if (_flight >= 1) { _stage = 3; _flight = 0; }
            }
            else
            {
                _flight += delta;
                if (_flight >= .65f)
                {
                    float error = new Vector2(_landing.x - .5f, (_landing.y - .28f) * .65f).magnitude;
                    if (error <= .025f) Complete(2);
                    else if (error <= .085f) Complete();
                    else Mistake(true);
                }
            }
            if (Input.GetKeyDown(KeyCode.Space)) Throw();
        }

        void Throw()
        {
            if (!CanPlay) return;
            if (_stage == 0)
            {
                _aim = Mathf.PingPong(_aimPhase, 1) * 2 - 1;
                _stage = 1;
            }
            else if (_stage == 1)
            {
                float power = Mathf.PingPong(_powerPhase, 1);
                _landing = new Vector2(.5f + _aim * .32f + _wind * .16f, .28f + (.6f - power) * .55f);
                _flight = 0;
                _stage = 2;
            }
        }

        void DrawHorseshoe(Vector2 center, float angle)
        {
            const int segments = 12;
            for (int i = 0; i < segments; i++)
            {
                float a = (angle + 45 + i * 270f / segments) * Mathf.Deg2Rad;
                float b = (angle + 45 + (i + 1) * 270f / segments) * Mathf.Deg2Rad;
                Stroke(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 8,
                    center + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * 8, new Color(.86f, .88f, .81f), 3);
            }
        }

        protected override void DrawBoard(Rect board)
        {
            var ground = new Rect(board.x + 12, board.y + 26, board.width - 24, board.height - 106);
            Widgets.DrawBoxSolid(ground, new Color(.2f, .25f, .15f));
            var pin = Point(ground, new Vector2(.5f, .28f));
            for (int i = 0; i < 24; i++)
            {
                float a = i * Mathf.PI / 12, b = (i + 1) * Mathf.PI / 12;
                Stroke(pin + new Vector2(Mathf.Cos(a) * ground.width * .085f, Mathf.Sin(a) * ground.height * .085f / .65f),
                    pin + new Vector2(Mathf.Cos(b) * ground.width * .085f, Mathf.Sin(b) * ground.height * .085f / .65f), Accent, 1);
            }
            Widgets.DrawBoxSolid(new Rect(pin.x - 2, pin.y - 13, 4, 20), new Color(.7f, .74f, .71f));
            Widgets.DrawBoxSolid(new Rect(pin.x - 7, pin.y + 5, 14, 3), new Color(.08f, .12f, .08f));
            Widgets.Label(new Rect(board.x + 8, board.y + 1, board.width - 16, 23), "Rim3D_HorseshoesWind".Translate(
                _wind < 0 ? "←" : "→", Mathf.RoundToInt(Mathf.Abs(_wind) / .45f * 100)));
            if (_stage >= 2)
            {
                var end = Point(ground, _landing);
                float t = _stage == 3 ? 1 : _flight;
                var position = Vector2.Lerp(Point(ground, new Vector2(.5f, .95f)), end, t);
                Widgets.DrawBoxSolid(new Rect(position.x - 5, position.y, 10, 3), new Color(.06f, .09f, .05f));
                position.y -= Mathf.Sin(t * Mathf.PI) * ground.height * .65f;
                DrawHorseshoe(position, t * 540);
            }
            else
            {
                float aim = _stage == 0 ? Mathf.PingPong(_aimPhase, 1) * 2 - 1 : _aim;
                Stroke(Point(ground, new Vector2(.5f, .95f)), Point(ground, new Vector2(.5f + aim * .32f, .3f)),
                    new Color(.5f, .65f, .48f), 2);
                DrawHorseshoe(Point(ground, new Vector2(.5f, .95f)), 0);
            }
            var meter = new Rect(board.x + 16, board.yMax - 66, board.width - 32, 12);
            float value = _stage == 0 ? Mathf.PingPong(_aimPhase, 1) : Mathf.PingPong(_powerPhase, 1);
            Bar(meter, value, _stage == 0 ? new Color(.4f, .7f, .72f) : Accent);
            if (_stage == 1) Widgets.DrawBoxSolid(new Rect(meter.x + meter.width * .6f - 1, meter.y - 3, 2, 18), Color.white);
            string label = (_stage == 0 ? "Rim3D_HorseshoesAim" : _stage == 1 ? "Rim3D_HorseshoesThrow" : "Rim3D_HorseshoesFlight").Translate();
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && _stage < 2;
            if (Button(new Rect(board.x + 16, board.yMax - 41, board.width - 32, 30), label)) Throw();
            GUI.enabled = enabled;
        }
    }
}
