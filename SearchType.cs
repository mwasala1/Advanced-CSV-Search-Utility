using System.ComponentModel;

namespace AdvancedCsvSearch
{
    public enum SearchType
    {
        [Description("Exact Match")] ExactMatch,
        [Description("Is One Of (List)")] IsOneOf,
        [Description("Regex")] Regex,
        [Description("Greater Than (>)")] GreaterThan,
        [Description("Less Than (<)")] LessThan,
        [Description("Is Between")] IsBetween,
        [Description("On Date")] OnDate,
        [Description("Before Date")] BeforeDate,
        [Description("After Date")] AfterDate,
        [Description("Is Between Dates")] IsBetweenDates
    }
}
