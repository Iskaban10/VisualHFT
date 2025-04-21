﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VisualHFT.TriggerEngine
{
    public enum ConditionOperator
    {
        Equals,
        GreaterThan,
        LessThan,
        CrossesAbove,
        CrossesBelow
    }
    public enum TimeWindowUnit
    {
        Seconds,
        Milliseconds,
        Ticks,
        Minutes,
        Hours,
        Days
    }
    public enum ActionType
    {
        UIAlert,
        RestApi
        // Future: UI, LogFile, PluginCallback, Webhook, StrategyControl, etc.
    }
    public enum AlertSeverity
    {
        Info,
        Warning,
        Error
    }
    
}
