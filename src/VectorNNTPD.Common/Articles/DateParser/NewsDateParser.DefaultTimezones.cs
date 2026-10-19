namespace VectorNNTPD.Common.Articles.DateParser
{
    /// <content>
    /// Built-in trailing timezone-abbreviation table used during date canonicalization.
    /// </content>
    internal static partial class NewsDateParser
    {
        /// <summary>
        /// One trailing date abbreviation and the ASCII offset bytes written in its place.
        /// </summary>
        /// <param name="Abbreviation">Abbreviation bytes matched with ASCII case folding.</param>
        /// <param name="Offset">Replacement bytes, including the sign, such as <c>+00:00</c> or <c>+05:30</c>.</param>
        private readonly record struct TimezoneMapping(byte[] Abbreviation, byte[] Offset);

        /// <summary>
        /// Fixed abbreviation-to-offset table used by <c>SubstituteTimezoneAbbreviation</c>.
        /// The lookup compares the entire trailing letter token. Order does not express prefixes.
        /// <c>CST</c> is the North American abbreviation named by RFC 1036. Its <c>-06:00</c> mapping is the Issue #164 convention.
        /// The local RFC 1036 text does not state that numeric offset.
        /// <c>UTC+0</c> through <c>UTC+9</c> and <c>UTC-1</c> through <c>UTC-9</c> are retained and are not selected:
        /// the detector accepts only a trailing ASCII letter run, so those tokens never reach this table.
        /// </summary>
        private static readonly TimezoneMapping[] TimezoneMappings = CreateDefaultTimezoneMappings();

        /// <summary>
        /// Builds <see cref="TimezoneMappings"/> from the abbreviation/offset pairs in this method.
        /// Offsets are the conventional static values used for news dates. They are not a civil-timezone database,
        /// and they do not follow the machine's local zone.
        /// <c>MSD</c>, <c>BRST</c>, <c>WAST</c>, <c>IRDT</c>, <c>AZST</c>, and <c>AWDT</c> are historical daylight-time conventions, not current year-round offsets.
        /// Civil offsets varied by place and period, so those static values do not reconstruct the original local civil time for every article date.
        /// Ambiguous tokens that are absent on purpose:
        /// <c>AST</c>/<c>ADT</c> (Atlantic <c>-04:00</c>/<c>-03:00</c> and Arabia <c>+03:00</c>/<c>+04:00</c>),
        /// <c>GST</c> (Gulf <c>+04:00</c> and South Georgia <c>-02:00</c>),
        /// <c>NST</c>/<c>NDT</c> (Newfoundland and other historical uses),
        /// <c>AMT</c> (Armenia <c>+04:00</c> and Amazon <c>-04:00</c>),
        /// <c>ECT</c> (Ecuador <c>-05:00</c> and Eastern Caribbean <c>-04:00</c>),
        /// <c>WST</c> (Western Australia and Samoa), and <c>SST</c> (American Samoa <c>-11:00</c> and historical Samoa).
        /// <c>BST</c> is British Summer Time, not Bangladesh. <c>IST</c> keeps the existing India Standard Time mapping;
        /// Irish and Israel Standard Time use the same letters and are not distinguished.
        /// <c>PST</c> is Pacific, not the Philippines (<c>PHT</c>). China Standard Time is not <c>CST</c>; the distinct token is <c>BJT</c>.
        /// </summary>
        /// <returns>The table in match order.</returns>
        private static TimezoneMapping[] CreateDefaultTimezoneMappings() =>
        [
            // RFC 1036 names GMT and the North American abbreviations. CST is Central, not China.
            // Its -06:00 value is the Issue #164 convention; the local RFC 1036 text does not print that offset.
            new("UT"u8.ToArray(), "+00:00"u8.ToArray()),
            new("UTC"u8.ToArray(), "+00:00"u8.ToArray()),
            new("GMT"u8.ToArray(), "+00:00"u8.ToArray()),
            new("EST"u8.ToArray(), "-05:00"u8.ToArray()),
            new("EDT"u8.ToArray(), "-04:00"u8.ToArray()),
            new("CST"u8.ToArray(), "-06:00"u8.ToArray()),
            new("CDT"u8.ToArray(), "-05:00"u8.ToArray()),
            new("MST"u8.ToArray(), "-07:00"u8.ToArray()),
            new("MDT"u8.ToArray(), "-06:00"u8.ToArray()),
            new("PST"u8.ToArray(), "-08:00"u8.ToArray()),
            new("PDT"u8.ToArray(), "-07:00"u8.ToArray()),

            // Alaska and Hawaii. Single-letter military zones from RFC 5322 are excluded by the 2..8 letter rule.
            new("AKST"u8.ToArray(), "-09:00"u8.ToArray()),
            new("AKDT"u8.ToArray(), "-08:00"u8.ToArray()),
            new("HST"u8.ToArray(), "-10:00"u8.ToArray()),

            // Europe. BST is British Summer Time (+01:00), the mapping already used here, not Bangladesh Standard Time.
            new("WET"u8.ToArray(), "+00:00"u8.ToArray()),
            new("WEST"u8.ToArray(), "+01:00"u8.ToArray()),
            new("CET"u8.ToArray(), "+01:00"u8.ToArray()),
            new("CEST"u8.ToArray(), "+02:00"u8.ToArray()),
            new("MET"u8.ToArray(), "+01:00"u8.ToArray()),
            new("MEST"u8.ToArray(), "+02:00"u8.ToArray()),
            new("EET"u8.ToArray(), "+02:00"u8.ToArray()),
            new("EEST"u8.ToArray(), "+03:00"u8.ToArray()),
            new("BST"u8.ToArray(), "+01:00"u8.ToArray()),
            new("MSK"u8.ToArray(), "+03:00"u8.ToArray()),
            // Historical Moscow daylight, not a current year-round offset.
            new("MSD"u8.ToArray(), "+04:00"u8.ToArray()),
            new("TRT"u8.ToArray(), "+03:00"u8.ToArray()),

            // South America and Saint-Pierre. Offsets are the long-standing conventional abbreviation values.
            new("ART"u8.ToArray(), "-03:00"u8.ToArray()),
            new("BRT"u8.ToArray(), "-03:00"u8.ToArray()),
            // Historical Brazil daylight, not a current year-round offset.
            new("BRST"u8.ToArray(), "-02:00"u8.ToArray()),
            new("CLT"u8.ToArray(), "-04:00"u8.ToArray()),
            new("CLST"u8.ToArray(), "-03:00"u8.ToArray()),
            new("COT"u8.ToArray(), "-05:00"u8.ToArray()),
            new("PET"u8.ToArray(), "-05:00"u8.ToArray()),
            new("BOT"u8.ToArray(), "-04:00"u8.ToArray()),
            new("GYT"u8.ToArray(), "-04:00"u8.ToArray()),
            new("SRT"u8.ToArray(), "-03:00"u8.ToArray()),
            new("UYT"u8.ToArray(), "-03:00"u8.ToArray()),
            new("GFT"u8.ToArray(), "-03:00"u8.ToArray()),
            new("PMST"u8.ToArray(), "-03:00"u8.ToArray()),
            new("PMDT"u8.ToArray(), "-02:00"u8.ToArray()),

            // Africa. SAST has no daylight partner in this table.
            new("WAT"u8.ToArray(), "+01:00"u8.ToArray()),
            // Historical West Africa daylight, not a current year-round offset.
            new("WAST"u8.ToArray(), "+02:00"u8.ToArray()),
            new("CAT"u8.ToArray(), "+02:00"u8.ToArray()),
            new("EAT"u8.ToArray(), "+03:00"u8.ToArray()),
            new("SAST"u8.ToArray(), "+02:00"u8.ToArray()),

            // Middle East and South Asia, including the fractional offsets used on the wire.
            // IST remains India Standard Time. IDT is Israel Daylight and does not make IST mean Israel.
            new("IRST"u8.ToArray(), "+03:30"u8.ToArray()),
            // Historical Iran daylight, not a current year-round offset.
            new("IRDT"u8.ToArray(), "+04:30"u8.ToArray()),
            new("AFT"u8.ToArray(), "+04:30"u8.ToArray()),
            new("PKT"u8.ToArray(), "+05:00"u8.ToArray()),
            new("IST"u8.ToArray(), "+05:30"u8.ToArray()),
            new("NPT"u8.ToArray(), "+05:45"u8.ToArray()),
            new("LKT"u8.ToArray(), "+05:30"u8.ToArray()),
            new("MMT"u8.ToArray(), "+06:30"u8.ToArray()),
            new("BTT"u8.ToArray(), "+06:00"u8.ToArray()),
            new("IDT"u8.ToArray(), "+03:00"u8.ToArray()),
            new("AZT"u8.ToArray(), "+04:00"u8.ToArray()),
            // Historical Azerbaijan daylight, not a current year-round offset.
            new("AZST"u8.ToArray(), "+05:00"u8.ToArray()),
            new("GET"u8.ToArray(), "+04:00"u8.ToArray()),

            // South, Southeast, Central, and East Asia. BJT is Beijing. PHT is the Philippines, not PST.
            new("ICT"u8.ToArray(), "+07:00"u8.ToArray()),
            new("WIB"u8.ToArray(), "+07:00"u8.ToArray()),
            new("WITA"u8.ToArray(), "+08:00"u8.ToArray()),
            new("WIT"u8.ToArray(), "+09:00"u8.ToArray()),
            new("HKT"u8.ToArray(), "+08:00"u8.ToArray()),
            new("SGT"u8.ToArray(), "+08:00"u8.ToArray()),
            new("BJT"u8.ToArray(), "+08:00"u8.ToArray()),
            new("PHT"u8.ToArray(), "+08:00"u8.ToArray()),
            new("MYT"u8.ToArray(), "+08:00"u8.ToArray()),
            new("BNT"u8.ToArray(), "+08:00"u8.ToArray()),
            new("JST"u8.ToArray(), "+09:00"u8.ToArray()),
            new("KST"u8.ToArray(), "+09:00"u8.ToArray()),
            new("ULAT"u8.ToArray(), "+08:00"u8.ToArray()),
            new("HOVT"u8.ToArray(), "+07:00"u8.ToArray()),
            new("KGT"u8.ToArray(), "+06:00"u8.ToArray()),
            new("UZT"u8.ToArray(), "+05:00"u8.ToArray()),
            new("TMT"u8.ToArray(), "+05:00"u8.ToArray()),
            new("TJT"u8.ToArray(), "+05:00"u8.ToArray()),
            new("YEKT"u8.ToArray(), "+05:00"u8.ToArray()),
            new("OMST"u8.ToArray(), "+06:00"u8.ToArray()),
            new("NOVT"u8.ToArray(), "+07:00"u8.ToArray()),
            new("KRAT"u8.ToArray(), "+07:00"u8.ToArray()),
            new("IRKT"u8.ToArray(), "+08:00"u8.ToArray()),
            new("YAKT"u8.ToArray(), "+09:00"u8.ToArray()),
            new("VLAT"u8.ToArray(), "+10:00"u8.ToArray()),
            new("PETT"u8.ToArray(), "+12:00"u8.ToArray()),

            // Australia, New Zealand, and the Pacific. Fractional offsets are the zone's conventional standard or daylight value.
            new("AWST"u8.ToArray(), "+08:00"u8.ToArray()),
            // Historical Western Australia daylight, not a current year-round offset.
            new("AWDT"u8.ToArray(), "+09:00"u8.ToArray()),
            new("ACST"u8.ToArray(), "+09:30"u8.ToArray()),
            new("ACDT"u8.ToArray(), "+10:30"u8.ToArray()),
            new("AEST"u8.ToArray(), "+10:00"u8.ToArray()),
            new("AEDT"u8.ToArray(), "+11:00"u8.ToArray()),
            new("LHST"u8.ToArray(), "+10:30"u8.ToArray()),
            new("LHDT"u8.ToArray(), "+11:00"u8.ToArray()),
            new("NZST"u8.ToArray(), "+12:00"u8.ToArray()),
            new("NZDT"u8.ToArray(), "+13:00"u8.ToArray()),
            new("CHAST"u8.ToArray(), "+12:45"u8.ToArray()),
            new("CHADT"u8.ToArray(), "+13:45"u8.ToArray()),
            new("ChST"u8.ToArray(), "+10:00"u8.ToArray()),
            new("TAHT"u8.ToArray(), "-10:00"u8.ToArray()),
            new("MART"u8.ToArray(), "-09:30"u8.ToArray()),
            new("CKT"u8.ToArray(), "-10:00"u8.ToArray()),
            new("FJT"u8.ToArray(), "+12:00"u8.ToArray()),
            new("TOT"u8.ToArray(), "+13:00"u8.ToArray()),
            new("GILT"u8.ToArray(), "+12:00"u8.ToArray()),
            new("PHOT"u8.ToArray(), "+13:00"u8.ToArray()),
            new("LINT"u8.ToArray(), "+14:00"u8.ToArray()),
            new("NRT"u8.ToArray(), "+12:00"u8.ToArray()),

            // Letter-only matching never selects these. Numeric ±hhmm and ±hh:mm offsets are parsed by the date patterns.
            new("UTC+0"u8.ToArray(), "+00:00"u8.ToArray()),
            new("UTC+1"u8.ToArray(), "+01:00"u8.ToArray()),
            new("UTC+2"u8.ToArray(), "+02:00"u8.ToArray()),
            new("UTC+3"u8.ToArray(), "+03:00"u8.ToArray()),
            new("UTC+4"u8.ToArray(), "+04:00"u8.ToArray()),
            new("UTC+5"u8.ToArray(), "+05:00"u8.ToArray()),
            new("UTC+6"u8.ToArray(), "+06:00"u8.ToArray()),
            new("UTC+7"u8.ToArray(), "+07:00"u8.ToArray()),
            new("UTC+8"u8.ToArray(), "+08:00"u8.ToArray()),
            new("UTC+9"u8.ToArray(), "+09:00"u8.ToArray()),
            new("UTC-1"u8.ToArray(), "-01:00"u8.ToArray()),
            new("UTC-2"u8.ToArray(), "-02:00"u8.ToArray()),
            new("UTC-3"u8.ToArray(), "-03:00"u8.ToArray()),
            new("UTC-4"u8.ToArray(), "-04:00"u8.ToArray()),
            new("UTC-5"u8.ToArray(), "-05:00"u8.ToArray()),
            new("UTC-6"u8.ToArray(), "-06:00"u8.ToArray()),
            new("UTC-7"u8.ToArray(), "-07:00"u8.ToArray()),
            new("UTC-8"u8.ToArray(), "-08:00"u8.ToArray()),
            new("UTC-9"u8.ToArray(), "-09:00"u8.ToArray()),
        ];
    }
}
