namespace cAlgo.Robots
{
    /// <summary>
    /// Pure engulfing predicates - no cAlgo dependency so a console harness can link
    /// this file and unit-check the exact code the bot ships with.
    ///
    /// Definition (spec, used exactly, both directions):
    ///   bullish: close > prior_open AND open <= prior_close AND close > open
    ///   bearish: close < prior_open AND open >= prior_close AND close < open
    /// </summary>
    public static class EngulfingLogic
    {
        public static bool IsBullish(double priorOpen, double priorClose, double open, double close)
        {
            return close > priorOpen && open <= priorClose && close > open;
        }

        public static bool IsBearish(double priorOpen, double priorClose, double open, double close)
        {
            return close < priorOpen && open >= priorClose && close < open;
        }
    }
}
