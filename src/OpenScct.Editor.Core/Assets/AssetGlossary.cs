using System.Text;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// English glosses for the French (and some German and Polish) words in stock asset names, so a search for
/// "bottle" finds <c>bouteille</c> and the browser can show "fire extinguisher" next to <c>squ_mer_obj_extinct</c>.
/// Built-in words plus a user file of <c>french=english</c> lines (<c>%APPDATA%/OpenSCCT-Editor/fr-en.txt</c>, as RE+
/// kept <c>Translations/fr-en.txt</c>).
/// </summary>
public static class AssetGlossary
{
    private static readonly Lazy<Dictionary<string, string>> s_words = new(Load);

    /// <summary>The user's own word list.</summary>
    public static string UserFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSCCT-Editor", "fr-en.txt");

    /// <summary>The English reading of an asset path's group and name, or null when no word is known.</summary>
    public static string? Gloss(string path)
    {
        (_, string group, string name) = AssetPaths.Split(path);
        var words = new List<string>();
        bool any = false;
        foreach (string token in Tokens(group).Concat(Tokens(name)))
        {
            if (Translate(token) is { } english)
            {
                any = true;
                if (words.Count == 0 || words[^1] != english)
                {
                    words.Add(english);
                }
            }
            else if (token.Length > 2 && !token.All(char.IsAsciiDigit))
            {
                words.Add(token.ToLowerInvariant());
            }
        }

        return any ? string.Join(' ', words) : null;
    }

    /// <summary>The English word for one name part, or null.</summary>
    public static string? Translate(string word)
    {
        string key = Fold(word);
        Dictionary<string, string> words = s_words.Value;
        if (words.TryGetValue(key, out string? english))
        {
            return english;
        }

        // Plurals: caisses, tuyaux.
        if (key.Length > 4 && key.EndsWith('s') && words.TryGetValue(key[..^1], out english))
        {
            return english;
        }

        return key.Length > 4 && key.EndsWith('x') && words.TryGetValue(key[..^1], out english) ? english : null;
    }

    /// <summary>Splits a name into words at _, -, digits, dots and lowercase-to-uppercase steps.</summary>
    public static IEnumerable<string> Tokens(string name)
    {
        var sb = new StringBuilder();
        char prev = '\0';
        foreach (char c in name)
        {
            bool split = c is '_' or '-' or '.' or ' ' || char.IsAsciiDigit(c) != char.IsAsciiDigit(prev) && sb.Length > 0 ||
                char.IsUpper(c) && char.IsLower(prev);
            if (split && sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }

            if (c is not ('_' or '-' or '.' or ' '))
            {
                sb.Append(c);
            }

            prev = c;
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    /// <summary>Lower case without accents (é → e), so "dépôt" and "depot" match.</summary>
    private static string Fold(string word)
    {
        string d = word.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (char c in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> Load()
    {
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in BuiltIn.Split('\n'))
        {
            Add(words, line);
        }

        try
        {
            if (File.Exists(UserFile))
            {
                foreach (string line in File.ReadLines(UserFile))
                {
                    Add(words, line);
                }
            }
        }
        catch (IOException)
        {
            // A locked or unreadable user file only loses the user's words.
        }

        return words;
    }

    private static void Add(Dictionary<string, string> words, string line)
    {
        string t = line.Trim();
        int eq = t.IndexOf('=', StringComparison.Ordinal);
        if (t.Length == 0 || t[0] == '#' || eq <= 0)
        {
            return;
        }

        words[Fold(t[..eq].Trim())] = t[(eq + 1)..].Trim();
    }

    // French unless marked; the words the stock packages use most.
    private const string BuiltIn = """
        abri=shelter
        accueil=reception
        aeration=ventilation
        affiche=poster
        algeco=site hut
        ampoule=bulb
        applique=wall lamp
        appart=apartment
        arbre=tree
        armoire=wardrobe
        ascenceur=lift
        ascenseur=lift
        atelier=workshop
        bac=tray
        balcon=balcony
        banc=bench
        bande=strip
        barre=bar
        barriere=barrier
        baril=barrel
        bas=low
        bateau=boat
        batiment=building
        beton=concrete
        betonwall=concrete wall
        bidon=can
        blanc=white
        bleu=blue
        bloc=block
        bois=wood
        boite=box
        boitier=casing
        bord=edge
        bouche=vent
        bouteille=bottle
        bouteil=bottle
        boutique=shop
        boutic=shop
        brique=brick
        bureau=office desk
        bureaux=offices
        buro=office
        cablelec=power cable
        cadre=frame
        cadrevitre=window frame
        cagibi=cupboard
        caisse=crate
        camion=truck
        canape=sofa
        carton=cardboard box
        casier=locker
        casse=broken
        cassee=broken
        chaine=chain
        chaise=chair
        chambre=room
        chantier=building site
        chariot=trolley
        chemin=path
        chien=dog
        chiottes=toilets
        ciment=cement
        citerne=tank
        clair=light
        cloison=partition
        coffre=chest
        colonne=column
        comptoir=counter
        conduit=duct
        corde=rope
        cote=side
        couloir=corridor
        cour=courtyard
        courbe=curved
        coude=elbow joint
        cuisine=kitchen
        dalle=slab
        debut=start
        depot=storage
        dessous=underside
        dessus=top
        divers=misc
        droite=right
        eau=water
        echafaudage=scaffolding
        echelle=ladder
        eclairage=lighting
        ecran=screen
        egout=sewer
        egouts=sewers
        enceinte=speaker
        enseigne=shop sign
        entree=entrance
        entrepot=warehouse
        escalier=stairs
        esca=stairs
        escal=stairs
        etabli=workbench
        etage=floor
        etagere=shelf
        exterieur=outside
        extincteur=fire extinguisher
        extinct=fire extinguisher
        fauteuil=armchair
        faux=false
        fauxplafond=false ceiling
        fenetre=window
        feuille=leaf
        fil=wire
        flacon=flask
        flaque=puddle
        fleche=arrow
        fond=back
        foplaf=false ceiling
        gauche=left
        generateur=generator
        gnerateur=generator
        gravier=gravel
        grillage=wire fence
        grille=grate
        grilleair=air grate
        gris=grey
        grue=crane
        haut=high
        herbe=grass
        humide=damp
        indications=signs
        jaune=yellow
        lampadaire=street lamp
        lampe=lamp
        lavabo=sink
        lumiere=light
        lustre=chandelier
        machinerie=machinery
        marche=step
        materiel=equipment
        meuble=furniture
        meubles=furniture
        miroir=mirror
        moniteur=monitor
        moquette=carpet
        mur=wall
        muret=low wall
        neon=neon light
        noir=black
        objet=object
        objets=objects
        ordi=computer
        ordinateur=computer
        palette=pallet
        panneau=panel
        panneaux=panels
        pancarte=sign
        pancart=sign
        papier=paper
        papiers=papers
        parking=car park
        passerelle=walkway
        passerel=walkway
        petit=small
        pied=foot
        pierre=stone
        pilier=pillar
        piliers=pillars
        placard=cupboard
        plafond=ceiling
        plaf=ceiling
        plafonnier=ceiling light
        plancher=floor
        planchers=floors
        planche=plank
        plante=plant
        plaque=plate
        plateforme=platform
        plateau=tray
        plexi=perspex
        pont=bridge
        porte=door
        portail=gate
        poteau=post
        poubelle=bin
        poutre=beam
        poutr=beam
        principal=main
        prise=socket
        projecteur=floodlight
        projo=floodlight
        quai=platform
        rambarde=railing
        rampe=handrail
        remb=railing
        rembarde=railing
        reserve=storeroom
        rideau=curtain
        rouge=red
        rouille=rust
        route=road
        rue=street
        sale=dirty
        salle=room
        sas=airlock
        siege=seat
        signaletique=signage
        sol=floor
        sous=under
        soussol=basement
        tableau=board
        tablo=board
        tabelec=fuse box
        tache=stain
        taches=stains
        tapis=rug
        tas=pile
        toilettes=toilets
        toit=roof
        tole=sheet metal
        tour=tower
        trappe=hatch
        trottoir=pavement
        troue=holed
        tuyau=pipe
        tuyaux=pipes
        tuyo=pipe
        usine=factory
        ventilateur=fan
        ventil=fan
        verre=glass
        verriere=glass roof
        vert=green
        vieux=old
        vitre=glass pane
        vitrine=display case
        vitr=glass
        voiture=car
        volet=shutter
        volets=shutters
        voute=vault
        vout=vault
        # German
        aussen=outside
        boden=floor
        fahrstuhl=lift
        gelaender=railing
        keller=cellar
        wand=wall
        # Polish
        drzwi=door
        schody=stairs
        szafa=wardrobe
        ukryte=hidden
        """;
}
