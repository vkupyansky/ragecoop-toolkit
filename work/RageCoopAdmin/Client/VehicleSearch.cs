using System;
using System.Linq;
using System.Text;

namespace RageCoopAdmin.Client;

internal static class VehicleSearch
{
    // Search names are separate from the displayed names and the game's model codes.
    private static readonly (string Name, string Aliases)[] Names =
    {
        ("bmw", "бмв бэмвэ"),
        ("audi", "ауди"),
        ("mercedes", "мерседес мерс"),
        ("maybach", "майбах"),
        ("amg", "амг"),
        ("mclaren", "макларен"),
        ("dodge", "додж"),
        ("challenger", "челленджер челленжер"),
        ("hellcat", "хеллкэт хеллкат хелкат"),
        ("ford", "форд"),
        ("raptor", "раптор"),
        ("aston martin", "астон мартин астонмартин"),
        ("db12", "дб12"),
        ("porsche", "порше порш"),
        ("turbo", "турбо"),
        ("li auto", "ли авто лисян лисянг лиссян"),
        ("zeekr", "зикр зикер"),
        ("rolls royce", "роллс ройс роллсройс ролсройс"),
        ("ghost", "гост гоуст"),
        ("bentley", "бентли"),
        ("flying spur", "флайинг флаинг спур флайингспур"),
        ("bugatti", "бугатти"),
        ("mistral", "мистраль"),
        ("divo", "диво"),
        ("lamborghini", "ламборгини ламборджини ламбо"),
        ("centenario", "центенарио"),
        ("urus", "урус"),
        ("m3", "м3"),
        ("m5", "м5"),
        ("touring", "туринг"),
        ("x6", "х6"),
        ("x6 m", "х6м"),
        ("x6m", "х6м"),
        ("xm", "хм"),
        ("sl63", "сл63"),
        ("g63", "г63"),
        ("f80", "ф80"),
        ("rs6", "рс6"),
        ("c63", "с63 ц63"),
        ("c8", "с8 ц8"),
        ("s650", "с650"),
        ("l9", "л9"),
        ("s500", "с500"),
        ("c300", "с300 ц300"),
        ("e63", "е63"),
        ("w204", "в204"),
        ("w206", "в206"),
        ("w213", "в213"),
        ("w223", "в223"),
        ("w16", "в16")
    };

    internal static bool Matches(string name, string code, string query)
    {
        string normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0) { return false; }

        string normalizedName = Normalize(name);
        string words = " " + normalizedName + " ";
        string text = normalizedName + " " + Normalize(code);
        foreach ((string english, string aliases) in Names)
        {
            if (words.IndexOf(" " + english + " ", StringComparison.Ordinal) >= 0)
            {
                text += " " + aliases;
            }
        }

        // Every word must belong to this model; mixed Russian/Latin queries are valid.
        string searchable = Normalize(text);
        return normalizedQuery.Split(' ').All(word => searchable.IndexOf(word, StringComparison.Ordinal) >= 0);
    }

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        bool separator = false;
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separator && result.Length != 0) { result.Append(' '); }
                result.Append(character == 'ё' || character == 'Ё' ? 'е' : char.ToLowerInvariant(character));
                separator = false;
            }
            else { separator = true; }
        }

        return result.ToString();
    }
}
