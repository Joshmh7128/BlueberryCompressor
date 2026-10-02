using AutoCompressor.Core.Models;

namespace AutoCompressor.Core.Recognition;

/// <summary>
/// A built-in list of well-known titles, so popular shows are recognised offline without any lookup.
/// It is deliberately a starting point, not a database: the online lookup covers everything else.
/// </summary>
public static class KnownTitles
{
    private static readonly Dictionary<string, ContentType> Map = Build();

    public static ContentType? Lookup(string? title)
    {
        var key = FileNameParser.Normalize(title);
        return key.Length > 0 && Map.TryGetValue(key, out var type) ? type : null;
    }

    private static Dictionary<string, ContentType> Build()
    {
        var map = new Dictionary<string, ContentType>(StringComparer.Ordinal);
        void Add(ContentType type, string list)
        {
            foreach (var title in list.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                map[FileNameParser.Normalize(title)] = type;
        }

        Add(ContentType.Sitcom,
            "Friends|Seinfeld|The Office|The Office US|The Office UK|Parks and Recreation|Parks and Rec|The Big Bang Theory|" +
            "How I Met Your Mother|Brooklyn Nine-Nine|Brooklyn 99|Frasier|Cheers|Scrubs|Modern Family|Community|30 Rock|" +
            "Arrested Development|It's Always Sunny in Philadelphia|Curb Your Enthusiasm|Two and a Half Men|That '70s Show|" +
            "The Fresh Prince of Bel-Air|Everybody Loves Raymond|The King of Queens|Will & Grace|New Girl|Superstore|" +
            "Schitt's Creek|The Good Place|Veep|The IT Crowd|Black Books|Father Ted|Peep Show|Only Fools and Horses|" +
            "Fawlty Towers|Blackadder|Red Dwarf|The Golden Girls|Malcolm in the Middle|The Middle|Mom|Young Sheldon|" +
            "Abbott Elementary|Ted Lasso|What We Do in the Shadows|Silicon Valley|The Nanny|Home Improvement|Full House|" +
            "Married... with Children|Taxi|M*A*S*H|I Love Lucy|3rd Rock from the Sun|Spin City|Just Shoot Me|NewsRadio|" +
            "Wings|Becker|The Drew Carey Show|Roseanne|The Conners|Black-ish|Fresh Off the Boat|The Goldbergs|" +
            "Kim's Convenience|Corner Gas|Letterkenny|Workaholics|The League|Rules of Engagement|Mike & Molly|" +
            "2 Broke Girls|Happy Endings|Cougar Town|Raising Hope|My Name Is Earl|The Mindy Project|" +
            "Unbreakable Kimmy Schmidt|One Day at a Time|Ghosts|Derry Girls|The Inbetweeners|Friday Night Dinner|" +
            "Gavin & Stacey|Miranda|Outnumbered|Extras|Family Matters|Step by Step|Boy Meets World|Saved by the Bell|" +
            "The Jeffersons|All in the Family|Sanford and Son|Night Court|Mad About You|Dharma & Greg|Reba|" +
            "According to Jim|Still Standing|Yes, Dear|George Lopez|My Wife and Kids|The Bernie Mac Show|" +
            "Everybody Hates Chris|Hot in Cleveland|Last Man Standing|Man with a Plan|Life in Pieces|Speechless|" +
            "American Housewife|Trial & Error|Great News|Champions|The Vicar of Dibley|Keeping Up Appearances|" +
            "Yes Minister|Yes, Prime Minister|Dad's Army|Porridge|Open All Hours|Absolutely Fabulous|Spaced|" +
            "Not Going Out|Mrs. Brown's Boys|Detectorists|Parks and Recreation US");

        Add(ContentType.Animation,
            "The Simpsons|Family Guy|South Park|Futurama|Bob's Burgers|Rick and Morty|Archer|BoJack Horseman|" +
            "Adventure Time|Avatar: The Last Airbender|Avatar The Last Airbender|The Legend of Korra|American Dad|" +
            "American Dad!|King of the Hill|SpongeBob SquarePants|Gravity Falls|Steven Universe|Regular Show|" +
            "The Owl House|Phineas and Ferb|Batman: The Animated Series|Batman The Animated Series|" +
            "X-Men: The Animated Series|X-Men '97|Justice League|Justice League Unlimited|Teen Titans|Teen Titans Go!|" +
            "Samurai Jack|Dexter's Laboratory|The Powerpuff Girls|Courage the Cowardly Dog|Looney Tunes|Tom and Jerry|" +
            "Scooby-Doo, Where Are You!|Big Mouth|F Is for Family|Invincible|Arcane|Harley Quinn|" +
            "Star Wars: The Clone Wars|Star Wars The Clone Wars|Star Wars Rebels|Star Wars: The Bad Batch|Bluey|" +
            "Peppa Pig|PAW Patrol|The Amazing World of Gumball|Over the Garden Wall|Primal|Love, Death & Robots|" +
            "Disenchantment|Central Park|Solar Opposites|Star Trek: Lower Decks|The Venture Bros.|Robot Chicken|" +
            "Aqua Teen Hunger Force|Beavis and Butt-Head|Daria|Rugrats|Hey Arnold!|Kim Possible|DuckTales|Animaniacs|" +
            "Pinky and the Brain|The Dragon Prince|She-Ra and the Princesses of Power|Voltron: Legendary Defender|" +
            "Young Justice|Spider-Man: The Animated Series|The Boondocks|Smiling Friends|Hazbin Hotel|Helluva Boss|" +
            "Inside Job|Close Enough|Final Space|Tuca & Bertie|The Midnight Gospel|Infinity Train|Amphibia|" +
            "Kipo and the Age of Wonderbeasts|The Cleveland Show|Bluey|Ben 10|Johnny Bravo|Ed, Edd n Eddy|" +
            "The Fairly OddParents|Danny Phantom|Invader Zim|The Ren & Stimpy Show|Rocko's Modern Life|" +
            "Star vs. the Forces of Evil|Miraculous: Tales of Ladybug & Cat Noir|What If...?|Blue Eye Samurai|" +
            "Toy Story|Toy Story 2|Toy Story 3|Toy Story 4|Finding Nemo|Finding Dory|The Incredibles|Incredibles 2|" +
            "Up|WALL-E|Ratatouille|Inside Out|Inside Out 2|Coco|Soul|Monsters, Inc.|Cars|Shrek|Shrek 2|" +
            "Kung Fu Panda|How to Train Your Dragon|Frozen|Frozen II|Moana|Zootopia|Tangled|The Lion King|Aladdin|" +
            "Beauty and the Beast|The Little Mermaid|Spider-Man: Into the Spider-Verse|" +
            "Spider-Man: Across the Spider-Verse|The Lego Movie|Despicable Me|Minions|Encanto|Klaus|" +
            "The Iron Giant|Puss in Boots: The Last Wish|The Mitchells vs. the Machines");

        Add(ContentType.Anime,
            "Naruto|Naruto Shippuden|Naruto Shippuuden|Boruto|Boruto: Naruto Next Generations|One Piece|Bleach|" +
            "Bleach: Thousand-Year Blood War|Dragon Ball|Dragon Ball Z|Dragon Ball Super|Dragon Ball GT|Dragon Ball Z Kai|" +
            "Attack on Titan|Shingeki no Kyojin|Death Note|Fullmetal Alchemist|Fullmetal Alchemist: Brotherhood|" +
            "Fullmetal Alchemist Brotherhood|Cowboy Bebop|Neon Genesis Evangelion|My Hero Academia|" +
            "Boku no Hero Academia|Demon Slayer|Demon Slayer: Kimetsu no Yaiba|Kimetsu no Yaiba|Jujutsu Kaisen|" +
            "Hunter x Hunter|One-Punch Man|One Punch Man|Sword Art Online|Steins;Gate|Steins Gate|Code Geass|" +
            "Code Geass: Lelouch of the Rebellion|Tokyo Ghoul|Fairy Tail|Mob Psycho 100|Spy x Family|Chainsaw Man|" +
            "Frieren|Frieren: Beyond Journey's End|Sousou no Frieren|Vinland Saga|Haikyuu!!|Haikyu!!|" +
            "JoJo's Bizarre Adventure|JoJo no Kimyou na Bouken|Made in Abyss|Re:Zero|" +
            "Re:Zero - Starting Life in Another World|KonoSuba|Overlord|That Time I Got Reincarnated as a Slime|" +
            "Tensei Shitara Slime Datta Ken|Black Clover|Dr. Stone|The Promised Neverland|Violet Evergarden|" +
            "Your Lie in April|Clannad|Gintama|Monster|Berserk|Trigun|Samurai Champloo|" +
            "Ghost in the Shell: Stand Alone Complex|Sailor Moon|Pokemon|Pokémon|Yu-Gi-Oh!|Digimon Adventure|" +
            "Inuyasha|Rurouni Kenshin|Yu Yu Hakusho|Ranma 1/2|Lupin the Third|Lupin III|Detective Conan|Case Closed|" +
            "Doraemon|Mobile Suit Gundam|Gurren Lagann|Tengen Toppa Gurren Lagann|Kill la Kill|Psycho-Pass|" +
            "Parasyte|Parasyte: The Maxim|Erased|Noragami|Blue Lock|Oshi no Ko|Bocchi the Rock!|" +
            "Kaguya-sama: Love Is War|Mushoku Tensei|Mushoku Tensei: Jobless Reincarnation|Solo Leveling|Dandadan|" +
            "The Apothecary Diaries|Kusuriya no Hitorigoto|Fire Force|Soul Eater|Fate/Zero|Fate/stay night|" +
            "Puella Magi Madoka Magica|K-On!|Lucky Star|Toradora!|Bakemonogatari|Monogatari|Nichijou|Azumanga Daioh|" +
            "Cardcaptor Sakura|Fruits Basket|Ouran High School Host Club|Initial D|Slam Dunk|Kuroko's Basketball|" +
            "Kuroko no Basket|Hajime no Ippo|Akame ga Kill!|The Seven Deadly Sins|Nanatsu no Taizai|Tokyo Revengers|" +
            "Hell's Paradise|Delicious in Dungeon|Dungeon Meshi|Cyberpunk: Edgerunners|Cyberpunk Edgerunners|" +
            "Assassination Classroom|Food Wars!|Shokugeki no Soma|The Rising of the Shield Hero|No Game No Life|" +
            "Angel Beats!|Anohana|Darling in the Franxx|Elfen Lied|Hellsing|Hellsing Ultimate|Baccano!|Durarara!!|" +
            "Mushishi|Natsume's Book of Friends|Spice and Wolf|Serial Experiments Lain|Outlaw Star|" +
            "The Melancholy of Haruhi Suzumiya|Great Teacher Onizuka|Golden Kamuy|Kingdom|Dororo|Banana Fish|" +
            "Ranking of Kings|Odd Taxi|86|86 Eighty-Six|Lycoris Recoil|Call of the Night|Zom 100|Kaiju No. 8|" +
            "Wind Breaker|Shangri-La Frontier|Undead Unluck|Mashle|Sakamoto Days|" +
            "Spirited Away|Princess Mononoke|My Neighbor Totoro|Howl's Moving Castle|Kiki's Delivery Service|" +
            "Castle in the Sky|Nausicaa of the Valley of the Wind|Ponyo|The Wind Rises|Grave of the Fireflies|Akira|" +
            "Your Name|Your Name.|Kimi no Na wa|Weathering with You|Suzume|A Silent Voice|Perfect Blue|Paprika|" +
            "Ghost in the Shell|The Boy and the Heron|Wolf Children|The Girl Who Leapt Through Time|" +
            "Whisper of the Heart|Porco Rosso|The Tale of the Princess Kaguya|Millennium Actress|Tokyo Godfathers|" +
            "5 Centimeters per Second|Redline|Sword of the Stranger|Demon Slayer: Mugen Train|Jujutsu Kaisen 0");

        Add(ContentType.Drama,
            "Breaking Bad|Better Call Saul|The Sopranos|The Wire|Mad Men|Succession|House of Cards|The Crown|" +
            "Downton Abbey|Ozark|The West Wing|House|House M.D.|Grey's Anatomy|ER|Law & Order|" +
            "Law & Order: Special Victims Unit|Law & Order SVU|NCIS|CSI|CSI: Crime Scene Investigation|" +
            "Criminal Minds|Suits|The Good Wife|The Good Fight|This Is Us|Six Feet Under|Oz|Dexter|Homeland|Narcos|" +
            "Peaky Blinders|Sherlock|Fargo|True Detective|The Americans|Friday Night Lights|Lost|" +
            "The Handmaid's Tale|Big Little Lies|Mare of Easttown|The Bear|Yellowstone|Billions|Shameless|" +
            "Orange Is the New Black|Gilmore Girls|Desperate Housewives|The Newsroom|Boston Legal|Chicago Fire|" +
            "Chicago P.D.|Chicago Med|Bones|Castle|The Mentalist|Justified|Sons of Anarchy|The Shield|Deadwood|" +
            "Boardwalk Empire|Twin Peaks|The Leftovers|Mr. Robot|Killing Eve|The Queen's Gambit|Chernobyl|" +
            "Prison Break|The Killing|Broadchurch|Line of Duty|Happy Valley|Call the Midwife|Outlander|Bridgerton|" +
            "Euphoria|Severance|The White Lotus|The Morning Show|Squid Game|Money Heist|La Casa de Papel|Dark|" +
            "The Good Doctor|Blue Bloods|Elementary|White Collar|Burn Notice|Monk|Psych|Columbo|The Practice|" +
            "L.A. Law|NYPD Blue|Hill Street Blues|The Rookie|9-1-1|Station 19|Private Practice|Scandal|" +
            "How to Get Away with Murder|Revenge|Gossip Girl|One Tree Hill|The O.C.|Dawson's Creek|Parenthood|" +
            "Brothers & Sisters|Nashville|Empire|Power|Snowfall|The Night Of|Sharp Objects|Mindhunter|" +
            "Slow Horses|The Diplomat|Shrinking|Industry|Mad Men|Halt and Catch Fire|Rectify|The Knick|" +
            "Borgen|The Bridge|Babylon Berlin|Crash Landing on You|Inspector Morse|Endeavour|Vera|Midsomer Murders|" +
            "Poirot|Agatha Christie's Poirot|Foyle's War|Luther|Bodyguard|Unforgotten|Shetland|Death in Paradise");

        Add(ContentType.Cinematic,
            "Game of Thrones|House of the Dragon|The Mandalorian|Andor|Stranger Things|The Witcher|The Boys|" +
            "Westworld|The Expanse|Battlestar Galactica|Star Trek|Star Trek: The Next Generation|" +
            "Star Trek: Deep Space Nine|Star Trek: Voyager|Star Trek: Discovery|Star Trek: Strange New Worlds|" +
            "Star Trek: Picard|Star Trek: Enterprise|Doctor Who|Firefly|The Last of Us|The Walking Dead|Vikings|" +
            "The Lord of the Rings: The Rings of Power|The Rings of Power|Foundation|Shogun|Shōgun|Rome|Spartacus|" +
            "Daredevil|Loki|WandaVision|The Punisher|Altered Carbon|Lost in Space|The 100|Fringe|The X-Files|" +
            "Supernatural|Arrow|The Flash|Band of Brothers|The Pacific|Masters of the Air|Fallout|Silo|" +
            "For All Mankind|See|The Wheel of Time|His Dark Materials|Raised by Wolves|Halo|Obi-Wan Kenobi|Ahsoka|" +
            "The Book of Boba Fett|Black Sails|The Last Kingdom|3 Body Problem|Dune: Prophecy|Stargate SG-1|" +
            "Stargate Atlantis|Babylon 5|Farscape|Jessica Jones|Luke Cage|Moon Knight|Hawkeye|" +
            "The Falcon and the Winter Soldier|Agents of S.H.I.E.L.D.|The Umbrella Academy|Wednesday|" +
            "Shadow and Bone|Carnival Row|Black Mirror|Sense8|Watchmen|Peacemaker|Reacher|Jack Ryan|" +
            "The Terminal List|24|Person of Interest|Heroes|Smallville|Gotham|The Witcher: Blood Origin|" +
            "American Gods|Penny Dreadful|Sweet Tooth|Kingdom|Warrior|Into the Badlands|Vikings: Valhalla|" +
            "The Lord of the Rings|The Fellowship of the Ring|The Two Towers|The Return of the King|The Hobbit|" +
            "Star Wars|The Empire Strikes Back|Return of the Jedi|The Matrix|The Matrix Reloaded|Inception|" +
            "Interstellar|The Dark Knight|The Dark Knight Rises|Batman Begins|Dune|Dune: Part Two|Blade Runner|" +
            "Blade Runner 2049|Mad Max: Fury Road|Avatar|Avatar: The Way of Water|Gladiator|Jurassic Park|" +
            "The Avengers|Avengers: Infinity War|Avengers: Endgame|Top Gun: Maverick|Oppenheimer|Tenet|Dunkirk|" +
            "1917|Saving Private Ryan|Alien|Aliens|Terminator 2: Judgment Day|John Wick|Gravity|The Revenant");

        Add(ContentType.Documentary,
            "Planet Earth|Planet Earth II|Planet Earth III|Blue Planet|The Blue Planet|Blue Planet II|Our Planet|" +
            "Cosmos|Cosmos: A Spacetime Odyssey|Cosmos: A Personal Voyage|Life|Frozen Planet|Frozen Planet II|" +
            "The Vietnam War|The Civil War|Making a Murderer|Tiger King|The Last Dance|Chef's Table|" +
            "Wild Wild Country|Formula 1: Drive to Survive|Drive to Survive|How It's Made|NOVA|Frontline|" +
            "30 for 30|The World at War|Human Planet|Africa|Life on Earth|The Jinx|MythBusters|Seven Worlds, One Planet|" +
            "The Green Planet|Dynasties|Night on Earth|Life in Colour|A Perfect Planet|Prehistoric Planet|" +
            "Walking with Dinosaurs|Connections|Civilisation|The Ascent of Man|Baseball|Jazz|The Beatles: Get Back|" +
            "Free Solo|Icarus|The Social Dilemma|My Octopus Teacher|13th|Won't You Be My Neighbor?|Apollo 11|" +
            "They Shall Not Grow Old|Earth at Night in Color|Welcome to Earth|Our Universe|Wonders of the Universe|" +
            "Wonders of the Solar System|The Planets|Hostile Planet|One Strange Rock|Modern Marvels|" +
            "Anthony Bourdain: Parts Unknown|Parts Unknown|No Reservations|Somebody Feed Phil|Salt Fat Acid Heat");

        Add(ContentType.StillCam,
            "The Tonight Show|The Tonight Show Starring Jimmy Fallon|The Late Show|" +
            "The Late Show with Stephen Colbert|Late Night with Seth Meyers|The Daily Show|" +
            "Last Week Tonight with John Oliver|Last Week Tonight|Saturday Night Live|SNL|Conan|Jimmy Kimmel Live|" +
            "Jimmy Kimmel Live!|The Late Late Show|The Late Late Show with James Corden|Real Time with Bill Maher|" +
            "The Colbert Report|Jeopardy!|Wheel of Fortune|The Price Is Right|Family Feud|" +
            "Who Wants to Be a Millionaire|QI|Have I Got News for You|Would I Lie to You?|8 Out of 10 Cats|" +
            "8 Out of 10 Cats Does Countdown|Taskmaster|Whose Line Is It Anyway?|Mock the Week|Hot Ones|" +
            "The Joe Rogan Experience|Big Brother|The Bachelor|The Bachelorette|Keeping Up with the Kardashians|" +
            "The Real Housewives of Beverly Hills|Love Island|Love Is Blind|MasterChef|Hell's Kitchen|" +
            "Kitchen Nightmares|The Great British Bake Off|The Great British Baking Show|Top Chef|Chopped|" +
            "Shark Tank|Dragons' Den|American Idol|The Voice|America's Got Talent|Britain's Got Talent|" +
            "The X Factor|RuPaul's Drag Race|Queer Eye|Judge Judy|Dr. Phil|The Ellen DeGeneres Show|" +
            "The Oprah Winfrey Show|The View|60 Minutes|Antiques Roadshow|Pawn Stars|Storage Wars|" +
            "The Graham Norton Show|The Jonathan Ross Show|Countdown|University Challenge|Pointless|The Chase|" +
            "Only Connect|Never Mind the Buzzcocks|Meet the Press|Face the Nation|Charlie Rose|Inside the Actors Studio|" +
            "Comedians in Cars Getting Coffee|My Next Guest Needs No Introduction|Between Two Ferns|The Eric Andre Show|" +
            "Deal or No Deal|The Weakest Link|Match Game|Hollywood Squares|Is It Cake?|Nailed It!|The Circle|" +
            "Too Hot to Handle|Below Deck|Vanderpump Rules|Jersey Shore|90 Day Fiancé|Selling Sunset|Dancing with the Stars|" +
            "Strictly Come Dancing|The Apprentice|Project Runway|Come Dine with Me|Gogglebox|First Dates");

        return map;
    }
}
