namespace PersonaEngine.Lib.Configuration;

public record Live2DOptions
{
    public string ModelPath { get; set; } = "Resources/live2d";

    public string ModelName { get; set; } = "aria";

    /// <summary>
    ///     Zoom applied on top of the automatic fit. 1.0 shows the whole model;
    ///     higher values crop in (e.g. a full-body model can be framed to the
    ///     upper body by zooming in and shifting <see cref="ModelOffsetY" /> down).
    /// </summary>
    public double ModelZoom { get; set; } = 1.0;

    /// <summary>
    ///     Horizontal framing offset in view units. Positive moves the model right.
    /// </summary>
    public double ModelOffsetX { get; set; }

    /// <summary>
    ///     Vertical framing offset in view units. Negative moves the model down,
    ///     which brings the head/upper body into frame when zoomed in.
    /// </summary>
    public double ModelOffsetY { get; set; }

    /// <summary>
    ///     Multiplier for the idle head sway (ParamAngleX/Y/Z). 0 disables head motion.
    /// </summary>
    public double IdleHeadSway { get; set; } = 1.0;

    /// <summary>
    ///     Multiplier for the idle body sway (ParamBodyAngleX/Y/Z). 0 keeps the body still.
    /// </summary>
    public double IdleBodySway { get; set; } = 1.0;

    /// <summary>
    ///     Multiplier for the breathing amplitude (ParamBreath, chest). 0 disables it.
    /// </summary>
    public double IdleBreathSway { get; set; } = 1.0;

    /// <summary>
    ///     Multiplier for how fast the idle motion cycles. 1.0 is the framework default.
    /// </summary>
    public double IdleMotionSpeed { get; set; } = 1.0;

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;
}
