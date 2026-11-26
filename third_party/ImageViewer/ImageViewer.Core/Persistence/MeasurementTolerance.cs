#pragma warning disable CS1591

namespace ImageViewer.Models
{
    /// <summary>
    /// 测量公差的判定参数。
    /// Chinese: 标称值 Nominal + 上公差/下公差；下偏差为负（允许范围 = [Nominal - ToleranceMinus, Nominal + TolerancePlus]）。
    /// English: Nominal value with plus/minus tolerances for pass-fail judgement of measurements.
    /// </summary>
    public sealed class MeasurementTolerance
    {
        public double? Nominal { get; set; }

        public double? TolerancePlus { get; set; }

        public double? ToleranceMinus { get; set; }

        public bool IsEnabled => Nominal.HasValue;

        public bool IsValid =>
            (!Nominal.HasValue || double.IsFinite(Nominal.Value)) &&
            (!TolerancePlus.HasValue || (double.IsFinite(TolerancePlus.Value) && TolerancePlus.Value >= 0)) &&
            (!ToleranceMinus.HasValue || (double.IsFinite(ToleranceMinus.Value) && ToleranceMinus.Value >= 0));

        public bool IsWithinTolerance(double measured)
        {
            if (!IsEnabled || !IsValid || !double.IsFinite(measured))
            {
                return !IsEnabled;
            }

            double upper = Nominal!.Value + (TolerancePlus ?? 0);
            double lower = Nominal.Value - (ToleranceMinus ?? 0);
            return measured >= lower && measured <= upper;
        }

        /// <summary>
        /// 复制公差参数。
        /// Chinese: 公差面板会就地修改实例（如 Tolerance.Nominal 双向绑定），因此撤销快照与克隆必须持有独立副本，
        /// 否则后续编辑会同时改到快照，撤销无法还原。
        /// English: The tolerance editor mutates the instance in place, so snapshots and clones need their own copy.
        /// </summary>
        public MeasurementTolerance Clone()
        {
            return new MeasurementTolerance
            {
                Nominal = Nominal,
                TolerancePlus = TolerancePlus,
                ToleranceMinus = ToleranceMinus
            };
        }
    }
}

#pragma warning restore CS1591
