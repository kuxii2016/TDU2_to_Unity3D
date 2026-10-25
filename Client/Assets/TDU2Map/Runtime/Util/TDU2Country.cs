using System.Globalization;

namespace TDU2
{
    public enum TDU2Country : byte
    {
        undefined = 0,
        UNITED_ARAB_EMIRATES,
        ALBANIA,
        ARMENIA,
        ARGENTINA,
        AUSTRIA,
        AUSTRALIA,
        AZERBAIJAN,
        BELGIUM,
        BULGARIA,
        BAHRAIN,
        BRUNEI_DARUSSALAM,
        BOLIVIA,
        BRAZIL,
        BELARUS,
        BELIZE,
        CANADA,
        Island,
        SWITZERLAND,
        CHILE,
        CHINA,
        COLOMBIA,
        COSTA_RICA,
        CZECH_REPUBLIC,
        GERMANY = 24,
        DENMARK,
        DOMINICAN_REPUBLIC,
        ALGERIA,
        ECUADOR,
        ESTONIA,
        EGYPT,
        SPAIN,
        FINLAND,
        FAROE_ISLANDS,
        FRANCE,
        GREAT_BRITAIN,
        GEORGIA,
        GREECE,
        GUATEMALA,
        HONG_KONG,
        HONDURAS,
        CROATIA,
        HUNGARY,
        INDONESIA,
        IRELAND,
        ISRAEL,
        INDIA,
        IRAQ,
        IRAN,
        ICELAND,
        ITALY,
        JAMAICA,
        JORDAN,
        JAPAN,
        KENYA,
        KYRGYZSTAN,
        KOREA,
        KUWAIT,
        KAZAKHSTAN,
        LEBANON,
        LIECHTENSTEIN,
        LITHUANIA,
        LUXEMBOURG,
        LATVIA,
        LIBYA,
        MOROCCO,
        MONACO,
        MACEDONIA,
        MONGOLIA,
        MACAU,
        MALDIVES,
        MEXICO,
        MALAYSIA,
        NICARAGUA,
        NETHERLANDS,
        NORWAY,
        NEW_ZEALAND,
        OMAN,
        PANAMA,
        PERU,
        PHILIPPINES,
        PAKISTAN,
        POLAND,
        PUERTO_RICO,
        PORTUGAL,
        PARAGUAY,
        QATAR,
        ROMANIA,
        RUSSIAN_FEDERATION = 88,
        SAUDI_ARABIA,
        SWEDEN,
        SINGAPORE,
        SLOVENIA,
        SLOVAK_REPUBLIC,
        BANGLADESH,
        EL_SALVADOR,
        SYRIA,
        THAILAND,
        TUNISIA,
        TURKEY,
        TRINIDAD_AND_TOBAGO,
        TAIWAN,
        UKRAINE,
        UNITED_STATES,
        URUGUAY,
        UZBEKISTAN,
        VENEZUELA,
        VIET_NAM,
        YEMEN,
        SOUTH_AFRICA,
        ZIMBABWE,
        SRI_LANKA,
        CYPRUS,
        MALTA
    }

    public static class TDU2CountryHelper
    {
        /// <summary>
        /// Ermittelt die Länder-ID anhand der Windows-Region.
        /// </summary>
        public static byte GetSystemCountryId()
        {
            try
            {
                string code =
                    RegionInfo.CurrentRegion.TwoLetterISORegionName;

                return (byte)GetCountryFromCode(code);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Ermittelt das Land anhand eines ISO-Ländercodes,
        /// beispielsweise DE, de-DE oder de_DE.
        /// </summary>
        public static TDU2Country GetCountryFromCode(string countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode))
                return TDU2Country.undefined;

            countryCode = countryCode.Trim().Replace('_', '-');

            // Bei de-DE den Länderteil DE verwenden.
            string[] parts = countryCode.Split('-');

            if (parts.Length > 1)
                countryCode = parts[parts.Length - 1];

            countryCode = countryCode.ToUpperInvariant();

            switch (countryCode)
            {
                case "AE": return TDU2Country.UNITED_ARAB_EMIRATES;
                case "AL": return TDU2Country.ALBANIA;
                case "AM": return TDU2Country.ARMENIA;
                case "AR": return TDU2Country.ARGENTINA;
                case "AT": return TDU2Country.AUSTRIA;
                case "AU": return TDU2Country.AUSTRALIA;
                case "AZ": return TDU2Country.AZERBAIJAN;
                case "BE": return TDU2Country.BELGIUM;
                case "BG": return TDU2Country.BULGARIA;
                case "BH": return TDU2Country.BAHRAIN;
                case "BN": return TDU2Country.BRUNEI_DARUSSALAM;
                case "BO": return TDU2Country.BOLIVIA;
                case "BR": return TDU2Country.BRAZIL;
                case "BY": return TDU2Country.BELARUS;
                case "BZ": return TDU2Country.BELIZE;
                case "CA": return TDU2Country.CANADA;
                case "CH": return TDU2Country.SWITZERLAND;
                case "CL": return TDU2Country.CHILE;
                case "CN": return TDU2Country.CHINA;
                case "CO": return TDU2Country.COLOMBIA;
                case "CR": return TDU2Country.COSTA_RICA;
                case "CZ": return TDU2Country.CZECH_REPUBLIC;
                case "DE": return TDU2Country.GERMANY;
                case "DK": return TDU2Country.DENMARK;
                case "DO": return TDU2Country.DOMINICAN_REPUBLIC;
                case "DZ": return TDU2Country.ALGERIA;
                case "EC": return TDU2Country.ECUADOR;
                case "EE": return TDU2Country.ESTONIA;
                case "EG": return TDU2Country.EGYPT;
                case "ES": return TDU2Country.SPAIN;
                case "FI": return TDU2Country.FINLAND;
                case "FO": return TDU2Country.FAROE_ISLANDS;
                case "FR": return TDU2Country.FRANCE;
                case "GB": return TDU2Country.GREAT_BRITAIN;
                case "GE": return TDU2Country.GEORGIA;
                case "GR": return TDU2Country.GREECE;
                case "GT": return TDU2Country.GUATEMALA;
                case "HK": return TDU2Country.HONG_KONG;
                case "HN": return TDU2Country.HONDURAS;
                case "HR": return TDU2Country.CROATIA;
                case "HU": return TDU2Country.HUNGARY;
                case "ID": return TDU2Country.INDONESIA;
                case "IE": return TDU2Country.IRELAND;
                case "IL": return TDU2Country.ISRAEL;
                case "IN": return TDU2Country.INDIA;
                case "IQ": return TDU2Country.IRAQ;
                case "IR": return TDU2Country.IRAN;
                case "IS": return TDU2Country.ICELAND;
                case "IT": return TDU2Country.ITALY;
                case "JM": return TDU2Country.JAMAICA;
                case "JO": return TDU2Country.JORDAN;
                case "JP": return TDU2Country.JAPAN;
                case "KE": return TDU2Country.KENYA;
                case "KG": return TDU2Country.KYRGYZSTAN;
                case "KR": return TDU2Country.KOREA;
                case "KW": return TDU2Country.KUWAIT;
                case "KZ": return TDU2Country.KAZAKHSTAN;
                case "LB": return TDU2Country.LEBANON;
                case "LI": return TDU2Country.LIECHTENSTEIN;
                case "LT": return TDU2Country.LITHUANIA;
                case "LU": return TDU2Country.LUXEMBOURG;
                case "LV": return TDU2Country.LATVIA;
                case "LY": return TDU2Country.LIBYA;
                case "MA": return TDU2Country.MOROCCO;
                case "MC": return TDU2Country.MONACO;
                case "MK": return TDU2Country.MACEDONIA;
                case "MN": return TDU2Country.MONGOLIA;
                case "MO": return TDU2Country.MACAU;
                case "MV": return TDU2Country.MALDIVES;
                case "MX": return TDU2Country.MEXICO;
                case "MY": return TDU2Country.MALAYSIA;
                case "NI": return TDU2Country.NICARAGUA;
                case "NL": return TDU2Country.NETHERLANDS;
                case "NO": return TDU2Country.NORWAY;
                case "NZ": return TDU2Country.NEW_ZEALAND;
                case "OM": return TDU2Country.OMAN;
                case "PA": return TDU2Country.PANAMA;
                case "PE": return TDU2Country.PERU;
                case "PH": return TDU2Country.PHILIPPINES;
                case "PK": return TDU2Country.PAKISTAN;
                case "PL": return TDU2Country.POLAND;
                case "PR": return TDU2Country.PUERTO_RICO;
                case "PT": return TDU2Country.PORTUGAL;
                case "PY": return TDU2Country.PARAGUAY;
                case "QA": return TDU2Country.QATAR;
                case "RO": return TDU2Country.ROMANIA;
                case "RU": return TDU2Country.RUSSIAN_FEDERATION;
                case "SA": return TDU2Country.SAUDI_ARABIA;
                case "SE": return TDU2Country.SWEDEN;
                case "SG": return TDU2Country.SINGAPORE;
                case "SI": return TDU2Country.SLOVENIA;
                case "SK": return TDU2Country.SLOVAK_REPUBLIC;
                case "BD": return TDU2Country.BANGLADESH;
                case "SV": return TDU2Country.EL_SALVADOR;
                case "SY": return TDU2Country.SYRIA;
                case "TH": return TDU2Country.THAILAND;
                case "TN": return TDU2Country.TUNISIA;
                case "TR": return TDU2Country.TURKEY;
                case "TT": return TDU2Country.TRINIDAD_AND_TOBAGO;
                case "TW": return TDU2Country.TAIWAN;
                case "UA": return TDU2Country.UKRAINE;
                case "US": return TDU2Country.UNITED_STATES;
                case "UY": return TDU2Country.URUGUAY;
                case "UZ": return TDU2Country.UZBEKISTAN;
                case "VE": return TDU2Country.VENEZUELA;
                case "VN": return TDU2Country.VIET_NAM;
                case "YE": return TDU2Country.YEMEN;
                case "ZA": return TDU2Country.SOUTH_AFRICA;
                case "ZW": return TDU2Country.ZIMBABWE;
                case "LK": return TDU2Country.SRI_LANKA;
                case "CY": return TDU2Country.CYPRUS;
                case "MT": return TDU2Country.MALTA;

                default: return TDU2Country.undefined;
            }
        }
    }
}
