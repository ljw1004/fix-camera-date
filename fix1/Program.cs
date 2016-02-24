﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Threading;
using System.IO;
using System.Xml.Linq;

static partial class Program
{
    static void Main(string[] args)
    {
        if (BingMapsKey == "") Console.WriteLine("THIS VERSION HAS BEEN BUILT WITHOUT GPS SUPPORT");
        string cmdFn = "", cmdPattern = "", cmdError = ""; TimeSpan? cmdOffset = null;
        var cmdArgs = new LinkedList<string>(args);
        // Get the filename
        if (cmdArgs.Count > 0 && !cmdArgs.First.Value.StartsWith("/")) { cmdFn = cmdArgs.First.Value; cmdArgs.RemoveFirst(); }
        // Search for further switches
        while (cmdError == "" && cmdArgs.Count > 0)
        {
            var cmdSwitch = cmdArgs.First.Value; cmdArgs.RemoveFirst();
            if (cmdSwitch == "/rename")
            {
                if (cmdPattern != "") { cmdError = "duplicate /rename"; break; }
                cmdPattern = "%{datetime} - %{fn} - %{place}";
                if (cmdArgs.Count > 0 && !cmdArgs.First.Value.StartsWith("/")) { cmdPattern = cmdArgs.First.Value; cmdArgs.RemoveFirst(); }
            }
            else if (cmdSwitch.StartsWith("/day") || cmdSwitch.StartsWith("/hour") || cmdSwitch.StartsWith("/minute"))
            {
                var len = 0; Func<int, TimeSpan> mkts = (n) => default(TimeSpan);
                if (cmdSwitch.StartsWith("/day")) { len = 4; mkts = (n) => TimeSpan.FromDays(n); }
                if (cmdSwitch.StartsWith("/hour")) { len = 5; mkts = (n) => TimeSpan.FromHours(n); }
                if (cmdSwitch.StartsWith("/minute")) { len = 7; mkts = (n) => TimeSpan.FromMinutes(n); }
                var snum = cmdSwitch.Substring(len);
                if (!snum.StartsWith("+") && !snum.StartsWith("-")) { cmdError = cmdSwitch; break; }
                var num = 0; if (!int.TryParse(snum, out num)) { cmdError = cmdSwitch; break; }
                cmdOffset = cmdOffset.HasValue ? cmdOffset : new TimeSpan(0);
                cmdOffset = cmdOffset + mkts(num);
            }
            else if (cmdSwitch == "/?")
            {
                cmdFn = "";
            }
            else
            {
                cmdError = cmdSwitch; break;
            }
        }
        if (cmdError != "") { Console.WriteLine("Unrecognized command: {0}", cmdError); return; }
        if (cmdArgs.Count > 0) { throw new Exception("Failed to parse command line"); }
        if (cmdFn == "")
        {
            Console.WriteLine("FixCameraDate \"a.jpg\" [/rename [\"pattern\"]] [/day+n] [/hour+n] [/minute+n]");
            Console.WriteLine("  Filename can include * and ? wildcards");
            Console.WriteLine("  /rename: pattern defaults to \"%{datetime} - %{fn} - %{place}\" and");
            Console.WriteLine("           can include %{date/time/year/month/day/hour/minute/second/place}");
            Console.WriteLine("  /day,/hour,/minute: adjust the timestamp; can be + or -");
            Console.WriteLine();
            Console.WriteLine("EXAMPLES:");
            Console.WriteLine("FixCameraDate \"a.jpg\"");
            Console.WriteLine("FixCameraDate \"*.jpg\" /rename \"%{date} - %{time} - %{fn}.jpg\"");
            Console.WriteLine("FixCameraDate \"*D*.mov\" /hour+8 /rename");
            return;
        }

        string globPath = "", globMatch = cmdFn;
        if (globMatch.Contains("\\"))
        {
            globPath = Path.GetDirectoryName(globMatch); globMatch = Path.GetFileName(globMatch);
        }
        else
        {
            globPath = Environment.CurrentDirectory;
        }
        var globFiles = Directory.GetFiles(globPath, globMatch);
        if (globFiles.Length == 0) Console.WriteLine("Not found - \"{0}\"", cmdFn);

        var filesToDo = new Queue<FileToDo>();
        var gpsToDo = new Dictionary<int, FileToDo>();
        var gpsNextRequestId = 1;
        foreach (var globFile in globFiles) filesToDo.Enqueue(new FileToDo { fn = globFile });

    }

    static HttpClient http = new HttpClient();

    static void DoGps(Dictionary<int, FileToDo> gpsToDo0, Queue<FileToDo> filesToDo)
    {
        var gpsToDo = new Dictionary<int, FileToDo>(gpsToDo0);
        gpsToDo0.Clear();
        Console.Write($"Looking up {gpsToDo.Count} GPS places");

        // Send the request
        var queryData = "Bing Spatial Data Services, 2.0\r\n";
        queryData += "Id|GeocodeRequest/Culture|ReverseGeocodeRequest/IncludeEntityTypes|ReverseGeocodeRequest/Location/Latitude|ReverseGeocodeRequest/Location/Longitude|GeocodeResponse/Address/Neighborhood|GeocodeResponse/Address/Locality|GeocodeResponse/Address/AdminDistrict|GeocodeResponse/Address/CountryRegion\r\n";
        foreach (var kv in gpsToDo)
        {
            queryData += $"{kv.Key}|en-US|neighborhood|{kv.Value.gpsCoordinates.Latitude:0.000000}|{kv.Value.gpsCoordinates.Longitude:0.000000}\r\n";
        }
        var queryUri = $"http://spatial.virtualearth.net/REST/v1/dataflows/geocode?input=pipe&key={BingMapsKey}";
        Console.Write(".");
        var statusResp = http.PostAsync(queryUri, new StringContent(queryData)).GetAwaiter().GetResult();
        if (!statusResp.IsSuccessStatusCode) { Console.WriteLine($"ERROR {statusResp.StatusCode} - {statusResp.ReasonPhrase}"); return; }
        if (string.IsNullOrEmpty(statusResp.Headers.Location?.ToString()) { Console.WriteLine(" ERROR - no location"); return; }
        var statusUri = statusResp.Headers.Location.ToString();
        Console.Write(".");

        // Ping the location until we get somewhere
        var resultUri = "";
        while (true)
        {
            Thread.Sleep(2000);
            Console.Write(".");
            var statusRaw = http.GetStringAsync($"{statusUri}?key={BingMapsKey}&output=xml").GetAwaiter().GetResult();
            Console.Write(".");
            var statusXml = XDocument.Parse(statusRaw);
            var status = statusXml.Descendants(XName.Get("Status", "http://schemas.microsoft.com/search/local/ws/rest/v1")).FirstOrDefault()?.Value;
            if (status == null) { Console.WriteLine("ERROR didn't find status"); return; }
            if (status == "Pending") continue;
            if (status == "Failed") { Console.WriteLine("ERROR 'Failed'"); return; }
            resultUri = (from link in statusXml.Descendants(XName.Get("Link", "http://schemas.microsoft.com/search/local/ws/rest/v1"))
                         where link.Attribute("role")?.Value == "output" && link.Attribute("name")?.Value == "succeeded"
                         select link.Value).FirstOrDefault();
            break;
        }
        if (string.IsNullOrEmpty(resultUri)) { Console.WriteLine("ERROR no results"); return; }

        var resultRaw = http.GetStringAsync($"{resultUri}?key={BingMapsKey}&output=json").GetAwaiter().GetResult();
        Console.Write(".");
        var resultLines = resultRaw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Skip(2).ToArray();
        foreach (var result in resultLines)
        {
            var parts = result.Split(new[] { '|' });
            var parts2 = new List<string>();
            var id = int.Parse(parts[0]);
            var neighborhood = parts[5]; // Capitol Hill
            var locality = parts[6]; // Seattle
            var adminDistrict = parts[7]; // WA
            var country = parts[8]; // United States
            if (!string.IsNullOrEmpty(neighborhood)) parts2.Add(neighborhood);
            if (!string.IsNullOrEmpty(locality)) parts2.Add(locality);
            if (!string.IsNullOrEmpty(adminDistrict)) parts2.Add(adminDistrict);
            if (!string.IsNullOrEmpty(country)) parts2.Add(country);
            var place = String.Join(", ", parts2);
            if (!string.IsNullOrEmpty(place))
            {
                gpsToDo[id].hasGpsResult = place;
                filesToDo.Enqueue(gpsToDo[id]);
            }
        }
        Console.WriteLine("done");
    }


    delegate string PartGenerator(string fn, DateTime dt, string place);
    delegate int MatchFunction(string remainder); // -1 for no-match, otherwise is the number of characters gobbled up
    delegate bool UpdateTimeFunc(Stream stream, TimeSpan off);

    readonly Tuple<DateTimeKind?, UpdateTimeFunc, GpsCoordinates> EmptyResult = new Tuple<DateTimeKind?, UpdateTimeFunc, GpsCoordinates>(null, () => false, null);

    class GpsCoordinates
    {
        public double Latitude;
        public double Longitude;
    }

    class PatternPart
    {
        public PartGenerator generator;
        public MatchFunction matcher;
        public string pattern;
    }

    class FileToDo
    {
        // (1) Upon first creation, FileToDo merely has "fn"
        // (2) After initial scan, it also has "localTime, setter, gpsCoordinates"
        // This information might be enough for the program to complete its work on this file.
        // (3) If not, then the file gets stored in a "to-gps" queue.
        // After gps results are back, then gpsResult is populated
        public string fn;

        public bool hasInitialScan;
        public DateTime localTime;
        public UpdateTimeFunc setter;
        public GpsCoordinates gpsCoordinates;

        public string hasGpsResult;
    }


    static Tuple<DateTimeKind, UpdateTimeFunc> FilestampTime(string fn)
    {
        var creationTime = File.GetCreationTime(fn);
        var writeTime = File.GetLastWriteTime(fn);
        var winnerTime = creationTime;
        if (writeTime < winnerTime) winnerTime = writeTime;
        var localTime = DateTimeKind.Utc(winnerTime.ToUniversalTime()); // Although they're stored in UTC on disk, the APIs give us local - time
        //
        // BUG COMPATIBILITY: Filestamp times are never as good as metadata times.
        // Windows Phone doesn't store metadata, but it does use filenames of the form "WP_20131225".
        // If we find that, we'll use it.
        int year = 0, month = 0, day = 0;
        bool hasWpName = false, usedFilenameTime = false;
        var regex = new System.Text.RegularExpressions.Regex(@"WP_20\d\d\d\d\d\d");
        if (regex.IsMatch(fn))
        {
            var i = fn.IndexOf("WP_20") + 3;
            if (fn.Length >= i + 8)
            {
                hasWpName = true;
                var s = fn.Substring(i, 8);
                year = int.Parse(s.Substring(0, 4));
                month = int.Parse(s.Substring(4, 2));
                day = int.Parse(s.Substring(6, 2));

                if (winnerTime.Year == year && winnerTime.Month == month && winnerTime.Day == day)
                {
                    // good, the filestamp agrees with the filename
                }
                else
                {
                    localTime = DateTimeKind.Unspecified(new DateTime(year, month, day, 12, 0, 0, System.DateTimeKind.Unspecified));
                    usedFilenameTime = true;
                }
            }
        }

        //
        UpdateTimeFunc lambda = (file2, off) =>
        {
            if (hasWpName)
            {
                var nt = winnerTime + off;
                if (usedFilenameTime || nt.Year != year || nt.Month != month || nt.Day != day)
                {
                    Console.WriteLine("Unable to modify time of file, since time was derived from filename"); return false;
                }
            }
            File.SetCreationTime(fn, creationTime + off);
            File.SetLastWriteTime(fn, writeTime + off);
            return true;
        };

        return Tuple.Create(localTime, lambda);
    }


    static Tuple<DateTimeKind?, UpdateTimeFunc, GpsCoordinates> ExifTime(Stream file, long start, long fend)
    {
        DateTime? timeLastModified=null, timeOriginal=null, timeDigitized=null;
        long posLastModified = 0, posOriginal = 0, posDigitized = 0;
        string gpsNS = "", gpsEW = "";
        double? gpsLatVal = null, gpsLongVal = null;

        var pos = start + 2;
        while (true) // Iterate through the EXIF markers
        {
            if (pos + 4 > fend) break;
            file.Seek(pos, SeekOrigin.Begin);
            var marker = file.Read2byte();
            var msize = file.Read2byte();
            //Console.WriteLine("EXIF MARKER {0:X}", marker)
            if (pos + msize > fend) break;
            var mbuf_pos = pos;
            pos += 2 + msize;
            if (marker == 0xFFDA) break; // image data follows this marker; we can stop our iteration
            if (marker != 0xFFE1) continue; // we're only interested in exif markers
            if (msize < 14) continue;
            var exif1 = file.Read4byte(); if (exif1 != 0x45786966) continue; // exif marker should start with this header "Exif"
            var exif2 = file.Read2byte(); if (exif2 != 0) continue;  // and with this header
            var exif3 = file.Read4byte();
            var ExifDataIsLittleEndian = false;
            if (exif3 == 0x49492A00) ExifDataIsLittleEndian = true;
            else if (exif3 == 0x4D4D002A) ExifDataIsLittleEndian = false;
            else continue; // unrecognized byte-order
            var ipos = file.Read4byte(ExifDataIsLittleEndian);
            if (ipos + 12 >= msize) continue;  // error  in tiff header
            //
            // Format of EXIF is a chain of IFDs. Each consists of a number of tagged entries.
            // One of the tagged entries may be "SubIFDpos = &H..." which gives the address of the
            // next IFD in the chain; if this entry is absent or 0, then we're on the last IFD.
            // Another tagged entry may be "GPSInfo = &H..." which gives the address of the GPS IFD
            //
            uint subifdpos = 0;
            uint gpsifdpos = 0;
            while (true) // iterate through the IFDs
            {
                //Console.WriteLine("  IFD @{0:X}\n", ipos)
                var ibuf_pos = mbuf_pos + 10 + ipos;
                file.Seek(ibuf_pos, SeekOrigin.Begin);
                var nentries = file.Read2byte(ExifDataIsLittleEndian);
                if (10 + ipos + 2 + nentries * 12 + 4 >= msize) break;  // error in ifd header
                file.Seek(ibuf_pos + 2 + nentries * 12, SeekOrigin.Begin);
                ipos = file.Read4byte(ExifDataIsLittleEndian);
                for (var i = 0; i < nentries; i++)
                {
                    var ebuf_pos = ibuf_pos + 2 + i * 12;
                    file.Seek(ebuf_pos, SeekOrigin.Begin);
                    var tag = file.Read2byte(ExifDataIsLittleEndian);
                    var format = file.Read2byte(ExifDataIsLittleEndian);
                    var ncomps = file.Read4byte(ExifDataIsLittleEndian);
                    var data = file.Read4byte(ExifDataIsLittleEndian);
                    //Console.WriteLine("    TAG {0:X} format={1:X} ncomps={2:X} data={3:X}", tag, format, ncomps, data)
                    if (tag == 0x8769 && format == 4)
                    {
                        subifdpos = data;
                    }
                    else if (tag == 0x8825 && format == 4)
                    {
                        gpsifdpos = data;
                    }
                    else if ((tag == 1 || tag == 3) && format == 2 && ncomps == 2)
                    {
                        var s = ((char)((int)(data >> 24))).ToString();
                        if (tag == 1) gpsNS = s; else gpsEW = s;
                    }
                    else if ((tag == 2 || tag == 4) && format == 5 && ncomps == 3 && 10 + data + ncomps < msize)
                    {
                        var ddpos = mbuf_pos + 10 + data;

                        file.Seek(ddpos, SeekOrigin.Begin);
                        var degTop = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var degBot = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var minTop = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var minBot = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var secTop = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var secBot = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var deg = degTop / degBot + minTop / minBot / 60.0 + secTop / secBot / 3600.0;
                        if (tag == 2) gpsLatVal = deg;
                        else if (tag == 4) gpsLongVal = deg;
                    }
                    else if ((tag == 0x132 || tag == 0x9003 || tag == 0x9004) && format == 2 && ncomps == 20 && 10 + data + ncomps < msize)
                    {
                        var ddpos = mbuf_pos + 10 + data;
                        file.Seek(ddpos, SeekOrigin.Begin);
                        var buf = new byte[19]; file.Read(buf, 0, 19);
                        var s = System.Text.Encoding.ASCII.GetString(buf);
                        DateTime dd;
                        if (DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dd))
                        {
                            if (tag == 0x132) { timeLastModified = dd; posLastModified = ddpos; }
                            if (tag == 0x9003) { timeOriginal = dd; posOriginal = ddpos; }
                            if (tag == 0x9004) { timeDigitized = dd; posDigitized = ddpos; }
                            //Console.WriteLine("      {0}", dd)
                        }
                    }
                } // next
                if (ipos == 0)
                {
                    ipos = subifdpos; subifdpos = 0;
                    if (ipos == 0) { ipos = gpsifdpos; gpsifdpos = 0; }
                    if (ipos == 0) break; // indicates the last IFD in this marker
                }
            } // while
        }

        var winnerTime = timeLastModified;
        if (!winnerTime.HasValue || (timeDigitized.HasValue && timeDigitized.Value < winnerTime.Value)) winnerTime = timeDigitized;
        if (!winnerTime.HasValue || (timeOriginal.HasValue && timeOriginal.Value < winnerTime.Value)) winnerTime = timeOriginal;
        //
        var winnerTimeOffset = winnerTime.HasValue ? DateTimeKind.Unspecified(winnerTime.Value) : (DateTimeKind?)null;

        UpdateTimeFunc lambda = (file2, off) =>
        {
            if (timeLastModified.HasValue && posLastModified != 0)
            {
                var buf = Encoding.ASCII.GetBytes((timeLastModified.Value + off).ToString("yyyy:MM:dd HH:mm:ss"));
                file2.Seek(posLastModified, SeekOrigin.Begin);
                file2.Write(buf, 0, buf.Length);
            }
            if (timeOriginal.HasValue && posOriginal != 0)
            {
                var buf = Encoding.ASCII.GetBytes((timeOriginal.Value + off).ToString("yyyy:MM:dd HH:mm:ss"));
                file2.Seek(posOriginal, SeekOrigin.Begin);
                file2.Write(buf, 0, buf.Length);
            }
            if (timeDigitized.HasValue && posDigitized != 0)
            {
                var buf = Encoding.ASCII.GetBytes((timeDigitized.Value + off).ToString("yyyy:MM:dd HH:mm:ss"));
                file2.Seek(posDigitized, SeekOrigin.Begin);
                file2.Write(buf, 0, buf.Length);
            }
            return true;
        };

        GpsCoordinates gps = null;
        if ((gpsNS == "N" || gpsNS == "S") && gpsLatVal.HasValue && (gpsEW == "E" || gpsEW == "W") && gpsLongVal.HasValue)
        {
            gps = new GpsCoordinates();
            gps.Latitude = gpsNS == "N" ? gpsLatVal.Value : -gpsLatVal.Value;
            gps.Longitude = gpsEW == "E" ? gpsLongVal.Value : -gpsLongVal.Value;
        }

        return Tuple.Create(winnerTimeOffset, lambda, gps);
    }


    Function Mp4Time(file As IO.Stream, start As Long, fend As Long) As Tuple(Of DateTimeKind?, UpdateTimeFunc, GpsCoordinates)
        ' The file is made up of a sequence of boxes, with a standard way to find size and FourCC "kind" of each.
        ' Some box kinds contain a kind-specific blob of binary data. Other box kinds contain a sequence
        ' of sub-boxes. You need to look up the specs for each kind to know whether it has a blob or sub-boxes.
        ' We look for a top-level box of kind "moov", which contains sub-boxes, and then we look for its sub-box
        ' of kind "mvhd", which contains a binary blob. This is where Creation/ModificationTime are stored.
        Dim pos = start, payloadStart = 0L, payloadEnd = 0L, boxKind = ""
        '
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "ftyp" : pos = payloadEnd
        End While
        If boxKind <> "ftyp" Then Return EmptyResult
        Dim majorBrandBuf(3) As Byte
        file.Seek(payloadStart, SeekOrigin.Begin) : file.Read(majorBrandBuf, 0, 4)
        Dim majorBrand = Text.Encoding.ASCII.GetString(majorBrandBuf)
        '
        pos = start
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "moov" : pos = payloadEnd : End While
        If boxKind <> "moov" Then Return EmptyResult
        Dim moovStart = payloadStart, moovEnd = payloadEnd
        '
        pos = moovStart : fend = moovEnd
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "mvhd" : pos = payloadEnd : End While
        If boxKind <> "mvhd" Then Return EmptyResult
        Dim mvhdStart = payloadStart, mvhdEnd = payloadEnd
        '
        pos = moovStart : fend = moovEnd
        Dim cdayStart = 0L, cdayEnd = 0L
        Dim cnthStart = 0L, cnthEnd = 0L
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "udta" : pos = payloadEnd : End While
        If boxKind = "udta" Then
            Dim udtaStart = payloadStart, udtaEnd = payloadEnd
            '
            pos = udtaStart : fend = udtaEnd
            While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "©day" : pos = payloadEnd : End While
            If boxKind = "©day" Then cdayStart = payloadStart : cdayEnd = payloadEnd
            '
            pos = udtaStart : fend = udtaEnd
            While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "CNTH" : pos = payloadEnd : End While
            If boxKind = "CNTH" Then cnthStart = payloadStart : cnthEnd = payloadEnd
        End If

        ' The "mvhd" binary blob consists of 1byte (version, either 0 or 1), 3bytes (flags),
        ' and then either 4bytes (creation), 4bytes (modification)
        ' or 8bytes (creation), 8bytes (modification)
        ' If version=0 then it's the former, otherwise it's the later.
        ' In both cases "creation" and "modification" are big-endian number of seconds since 1st Jan 1904 UTC
        If mvhdEnd - mvhdStart< 20 Then Return EmptyResult
        file.Seek(mvhdStart + 0, SeekOrigin.Begin) : Dim version = file.ReadByte(), numBytes = If(version = 0, 4, 8)
        file.Seek(mvhdStart + 4, SeekOrigin.Begin)
        Dim creationFix1970 = False, modificationFix1970 = False
        Dim creationTime = file.ReadDate(numBytes, creationFix1970)
        Dim modificationTime = file.ReadDate(numBytes, modificationFix1970)
        ' COMPATIBILITY-BUG: The spec says that these times are in UTC.
        ' However, my Sony Cybershot merely gives them in unspecified time (i.e. local time but without specifying the timezone)
        ' Indeed its UI doesn't even let you say what the current UTC time is.
        ' I also noticed that my Sony Cybershot gives MajorBrand="MSNV", which isn't used by my iPhone or Canon or WP8.
        ' I'm going to guess that all "MSNV" files come from Sony, and all of them have the bug.
        Dim makeMvhdTime = Function(dt As DateTime) As DateTimeKind
                               If majorBrand = "MSNV" Then Return DateTimeKind.Unspecified(dt)
                               Return DateTimeKind.Utc(dt)
                           End Function

        ' The "©day" binary blob consists of 2byte (string-length, big-endian), 2bytes (language-code), string
        Dim dayTime As DateTimeKind? = Nothing
        Dim cdayStringLen = 0, cdayString = ""
        If cdayStart<> 0 AndAlso cdayEnd - cdayStart > 4 Then
            file.Seek(cdayStart + 0, SeekOrigin.Begin)
            cdayStringLen = file.Read2byte()
            If cdayStart + 4 + cdayStringLen <= cdayEnd Then
                file.Seek(cdayStart + 4, SeekOrigin.Begin)
                Dim buf = New Byte(cdayStringLen - 1) { }
    file.Read(buf, 0, cdayStringLen)
                cdayString = System.Text.Encoding.ASCII.GetString(buf)
                Dim d As DateTimeOffset : If DateTimeOffset.TryParse(cdayString, d) Then dayTime = DateTimeKind.Local(d)
            End If
        End If

        ' The "CNTH" binary blob consists of 8bytes of unknown, followed by EXIF data
        Dim cnthTime As DateTimeKind? = Nothing, cnthLambda As UpdateTimeFunc = Nothing
        If cnthStart<> 0 AndAlso cnthEnd - cnthStart > 16 Then
           Dim exif_ft = ExifTime(file, cnthStart + 8, cnthEnd)
            cnthTime = exif_ft.Item1 : cnthLambda = exif_ft.Item2
        End If

        Dim winnerTime As DateTimeKind? = Nothing
        If dayTime.HasValue Then
            Debug.Assert(dayTime.Value.dt.Kind = System.DateTimeKind.Local)
            winnerTime = dayTime
            ' prefer this best of all because it knows local time and timezone
        ElseIf cnthTime.HasValue Then
            Debug.Assert(cnthTime.Value.dt.Kind = System.DateTimeKind.Unspecified)
            winnerTime = cnthTime
            ' this is second-best because it knows local time, just not timezone
        Else
            ' Otherwise, we'll make do with a UTC time, where we don't know local-time when the pic was taken, nor timezone
            If creationTime.HasValue AndAlso modificationTime.HasValue Then
                winnerTime = makeMvhdTime(If(creationTime < modificationTime, creationTime.Value, modificationTime.Value))
            ElseIf creationTime.HasValue Then
                winnerTime = makeMvhdTime(creationTime.Value)
            ElseIf modificationTime.HasValue Then
                winnerTime = makeMvhdTime(modificationTime.Value)
            End If
        End If

        Dim lambda As UpdateTimeFunc =
            Function(file2, offset)
                If creationTime.HasValue Then
                    Dim dd = creationTime.Value + offset
                    file2.Seek(mvhdStart + 4, SeekOrigin.Begin)
                    file2.WriteDate(numBytes, dd, creationFix1970)
                End If
                If modificationTime.HasValue Then
                    Dim dd = modificationTime.Value + offset
                    file2.Seek(mvhdStart + 4 + numBytes, SeekOrigin.Begin)
                    file2.WriteDate(numBytes, dd, modificationFix1970)
                End If
                If Not String.IsNullOrWhiteSpace(cdayString) Then
                    Dim dd As DateTimeOffset
                    If DateTimeOffset.TryParse(cdayString, dd) Then
                        dd = dd + offset
                        Dim str2 = dd.ToString("yyyy-MM-ddTHH:mm:sszz00")
                        Dim buf2 = Text.Encoding.ASCII.GetBytes(str2)
                        If buf2.Length = cdayStringLen Then
                            file2.Seek(cdayStart + 4, SeekOrigin.Begin)
                            file2.Write(buf2, 0, buf2.Length)
                        End If
                    End If
                End If
                If cnthLambda IsNot Nothing Then cnthLambda(file2, offset)
                Return True
            End Function

        Return Tuple.Create(winnerTime, lambda, CType(Nothing, GpsCoordinates))
    End Function


    static bool Mp4ReadNextBoxInfo(Stream f, long pos, long fend, ref string boxKind, ref long payloadStart, ref long payloadEnd)
    {
        boxKind = "" : payloadStart = 0 : payloadEnd = 0
        If pos +8 > fend Then Return False
       Dim b(3) As Byte
        f.Seek(pos, SeekOrigin.Begin)
        f.Read(b, 0, 4) : If BitConverter.IsLittleEndian Then Array.Reverse(b)
        Dim size = BitConverter.ToUInt32(b, 0)
        f.Read(b, 0, 4)
        Dim kind = ChrW(b(0)) & ChrW(b(1)) & ChrW(b(2)) & ChrW(b(3))
        If size<> 1 Then
            If pos + size > fend Then Return False
            boxKind = kind : payloadStart = pos + 8 : payloadEnd = payloadStart + size - 8 : Return True
        End If
        If size = 1 AndAlso pos +16 <= fend Then
           ReDim b(7)
            f.Read(b, 0, 8) : If BitConverter.IsLittleEndian Then Array.Reverse(b)
            Dim size2 = CLng(BitConverter.ToUInt64(b, 0))
            If pos +size2 > fend Then Return False
           boxKind = kind : payloadStart = pos + 16 : payloadEnd = payloadStart + size2 - 16 : Return True
        End If
        Return False
    }



    static void Add<T, U, V>(this LinkedList<Tuple<T, U, V>> me, T arg1, U arg2, V arg3)
    {
        me.AddLast(Tuple.Create(arg1, arg2, arg3));
    }

    readonly static DateTime TZERO_1904_UTC = new DateTime(1904, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
    readonly static DateTime TZERO_1970_UTC = new DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);

    static ushort Read2byte(this Stream f, bool fileIsLittleEndian = false)
    {
        var b = new byte[2];
        f.Read(b, 0, 2);
        if (BitConverter.IsLittleEndian != fileIsLittleEndian) Array.Reverse(b);
        return BitConverter.ToUInt16(b, 0);
    }

    static uint Read4byte(this Stream f, bool fileIsLittleEndian = false)
    {
        var b = new byte[4];
        f.Read(b, 0, 4);
        if (BitConverter.IsLittleEndian != fileIsLittleEndian) Array.Reverse(b);
        return BitConverter.ToUInt32(b, 0);
    }

    static DateTime? ReadDate(this Stream f, int numBytes, ref bool fixed1970)
    {
        // COMPATIBILITY-BUG: The spec says that these are expressed in seconds since 1904.
        // But my brother's Android phone picks them in seconds since 1970.
        // I'm going to guess that all dates before 1970 should be 66 years in the future
        // Note: I'm applying this correction *before* converting to date. That's because,
        // what with leap-years and stuff, it doesn't feel safe the other way around.
        if (numBytes == 4)
        {
            var b = new byte[4];
            f.Read(b, 0, 4);
            var secs = BitConverter.ToUInt32(b, 0);
            if (secs == 0) return null;
            fixed1970 = (secs < (TZERO_1970_UTC - TZERO_1904_UTC).TotalSeconds);
            return fixed1970 ? TZERO_1970_UTC.AddSeconds(secs) : TZERO_1904_UTC.AddSeconds(secs);
        }
        else if (numBytes == 8)
        {
            var b = new byte[8];
            f.Read(b, 0, 8);
            var secs = BitConverter.ToUInt64(b, 0);
            if (secs == 0) return null;
            fixed1970 = (secs < (TZERO_1970_UTC - TZERO_1904_UTC).TotalSeconds);
            return fixed1970 ? TZERO_1970_UTC.AddSeconds(secs) : TZERO_1904_UTC.AddSeconds(secs);
        }
        else
        {
            throw new ArgumentException("numBytes");
        }
    }

    static void WriteDate(this Stream f, int numBytes, DateTime d, bool fix1970)
    {
        if (d.Kind != System.DateTimeKind.Utc) throw new ArgumentException("Can only write UTC dates");
        if (numBytes == 4)
        {
            var secs = (uint)(fix1970 ? d - TZERO_1970_UTC : d - TZERO_1904_UTC).TotalSeconds);
            var b = BitConverter.GetBytes(secs);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            f.Write(b, 0, 4);
        }
        else if (numBytes == 8)
        {
            var secs = (ulong)(fix1970 ? d - TZERO_1970_UTC : d - TZERO_1904_UTC).TotalSeconds);
            var b = BitConverter.GetBytes(secs);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            f.Write(b, 0, 8);
        }
        else
        {
            throw new ArgumentException("numBytes");
        }
    }


}


struct DateTimeKind
{
    public DateTime dt;
    public TimeSpan offset;
    // Three modes:
    // (1) Time known to be in UTC: DateTime.Kind=UTC, offset=0
    // (2) Time known to be in some specific timezone: DateTime.Kind=Local, offset gives that timezone
    // (3) Time where nothing about timezone is known: DateTime.Kind=Unspecified, offset=0

    public static DateTimeKind Utc(DateTime d)
    {
        var d2 = new DateTime(d.Ticks, System.DateTimeKind.Utc);
        return new DateTimeKind { dt = d2, offset = default(TimeSpan) };
    }
    public static DateTimeKind Unspecified(DateTime d)
    {
        var d2 = new DateTime(d.Ticks, System.DateTimeKind.Unspecified);
        return new DateTimeKind { dt = d2, offset = default(TimeSpan) };
    }
    public static DateTimeKind Local(DateTimeOffset d)
    {
        var d2 = new DateTime(d.Ticks, System.DateTimeKind.Local);
        return new DateTimeKind { dt = d2, offset = d.Offset };
    }

    public override string ToString()
    {
        if (dt.Kind == System.DateTimeKind.Utc) return dt.ToString("yyyy:MM:ddTHH:mm:ssZ");
        else if (dt.Kind == System.DateTimeKind.Unspecified) return dt.ToString("yyyy:MM:dd HH:mm:ss");
        else if (dt.Kind == System.DateTimeKind.Local) return dt.ToString("yyyy:MM:dd HH:mm:ss") + offset.Hours.ToString("+00;-00") + "00";
        else throw new Exception("Invalid DateTimeKind");
    }


}


/*

Module Module1

    Sub Test()
        For Each fn In
{"eg-android - 2013.12.28 - 15.48 PST.jpg", "eg-android - 2013.12.28 - 15.48 PST.mp4",
                        "eg-canon-ixus - 2013.12.15 - 07.30 PST.jpg", "eg-canon-ixus - 2013.12.15 - 07.30 PST.mov",
                        "eg-canon-powershot - 2013.12.28 - 15.51 PST.jpg", "eg-canon-powershot - 2013.12.28 - 15.51 PST.mov",
                        "eg-iphone4s - 2013.12.28 - 15.49 PST.jpg", "eg-iphone4s - 2013.12.28 - 15.49 PST.mov",
                        "eg-iphone5 - 2013.12.10 - 15.40 PST.jpg", "eg-iphone5 - 2013.12.09 - 15.21 PST.mov",
                        "eg-sony-cybershot - 2013.12.15 - 07.30 PST.jpg", "eg-sony-cybershot - 2013.12.15 - 07.30 PST.mp4",
                        "eg-wp8 - 2013.12.15 - 07.33 PST.jpg", "eg-wp8 - 2013.12.15 - 07.33 PST.mp4",
                        "eg-screenshot.png", "eg-notapic.txt"}
Dim ft = FilestampTime($"test\{fn}")?.Item1
            Dim mt = MetadataTimeAndGps($"test\{fn}")?.Item1
            Console.WriteLine($"{fn}{vbCrLf}    ft={ft}{vbCrLf}    mt={mt}")
        Next
    End Sub

    Sub Main(args As String())


        While filesToDo.Count > 0 OrElse gpsToDo.Count > 0
            If filesToDo.Count = 0 Then DoGps(gpsToDo, filesToDo)
            Dim fileToDo = filesToDo.Dequeue()

            If Not fileToDo.hasInitialScan Then
                fileToDo.hasInitialScan = True
                Dim mtt = MetadataTimeAndGps(fileToDo.fn)
                Dim ftt = FilestampTime(fileToDo.fn)
                If mtt Is Nothing Then Console.WriteLine("Not an image/video - ""{0}""", IO.Path.GetFileName(fileToDo.fn)) : Continue While
                Dim mt = mtt.Item1, ft = ftt.Item1
                If mt.HasValue Then
                    fileToDo.setter = mtt.Item2
                    fileToDo.gpsCoordinates = mtt.Item3
                    If mt.Value.dt.Kind = System.DateTimeKind.Unspecified OrElse mt.Value.dt.Kind = System.DateTimeKind.Local Then
                        ' If dt.kind=Unspecified (e.g. EXIF, Sony), then the time is by assumption already local from when the picture was shot
                        ' If dt.kind=Local (e.g. iPhone-MOV), then the time is local, and also indicates its timezone offset
                        fileToDo.localTime = mt.Value.dt
                    ElseIf mt.Value.dt.Kind = System.DateTimeKind.Utc Then
                        ' If dt.Kind=UTC (e.g. Android), then time is in UTC, and we don't know how to read timezone.
                        fileToDo.localTime = mt.Value.dt.ToLocalTime() ' Best we can do is guess the timezone of the computer
                    End If
                Else
                    fileToDo.setter = ftt.Item2
                    If ft.dt.Kind = System.DateTimeKind.Unspecified Then
                        ' e.g. Windows Phone when we got the date from the filename
                        fileToDo.localTime = ft.dt
                    ElseIf ft.dt.Kind = System.DateTimeKind.Utc Then
                        ' e.g. all other files where we got the date from the filestamp
                        fileToDo.localTime = ft.dt.ToLocalTime() ' the best we can do is guess that the photo was taken in the timezone as this computer now
                    Else
                        Throw New Exception("Expected filetimes to be in UTC")
                    End If
                End If
            End If

            ' The only thing that requires GPS is if (1) we're doing a rename, (2) the
            ' pattern includes place, (3) the file actually has a GPS signature
            If cmdPattern.Contains("%{place}") AndAlso fileToDo.gpsCoordinates IsNot Nothing AndAlso fileToDo.hasGpsResult Is Nothing AndAlso Not String.IsNullOrEmpty(BingMapsKey.BingMapsKey) Then
                gpsNextRequestId += 1
                gpsToDo.Add(gpsNextRequestId, fileToDo)
                If gpsToDo.Count >= 50 Then DoGps(gpsToDo, filesToDo)
                Continue While
            End If

            ' Otherwise, by assumption here, either we have GPS result or we don't need it

            If cmdPattern = "" AndAlso Not cmdOffset.HasValue Then
                Console.WriteLine("""{0}"": {1:yyyy.MM.dd - HH.mm.ss}", IO.Path.GetFileName(fileToDo.fn), fileToDo.localTime)
            End If


            If cmdOffset.HasValue Then
                Using file = New IO.FileStream(fileToDo.fn, IO.FileMode.Open, IO.FileAccess.ReadWrite)
                    Dim prevTime = fileToDo.localTime
                    Dim r = fileToDo.setter(file, cmdOffset.Value)
                    If r Then
                        fileToDo.localTime += cmdOffset.Value
                        If cmdPattern = "" Then Console.WriteLine("""{0}"": {1:yyyy.MM.dd - HH.mm.ss}, corrected from {2:yyyy.MM.dd - HH.mm.ss}", IO.Path.GetFileName(fileToDo.fn), fileToDo.localTime, prevTime)
                    End If
                End Using
            End If


            If cmdPattern <> "" Then
                ' Filename heuristics:
                ' (1) If the user omitted an extension from the rename string, then we re-use the one that was given to us
                ' (2) If the filename already matched our datetime format, then we figure out what was the base filename
                If Not cmdPattern.Contains("%{fn}") Then Console.WriteLine("Please include %{fn} in the pattern") : Return
                If cmdPattern.Contains("\") Then Console.WriteLine("Folders not allowed in pattern") : Return
                If cmdPattern.Split({"%{fn}"}, StringSplitOptions.None).Length<> 2 Then Console.WriteLine("Please include %{fn} only once in the pattern") : Return
                '
                ' 1. Extract out the extension
                Dim pattern = cmdPattern
                Dim patternExt As String = Nothing
                For Each potentialExt In {".jpg", ".mp4", ".mov", ".jpeg"}
If Not pattern.ToLower.EndsWith(potentialExt) Then Continue For
patternExt = pattern.Substring(pattern.Length - potentialExt.Length)
                    pattern = pattern.Substring(0, pattern.Length - potentialExt.Length)
                    Exit For
                Next
                If patternExt Is Nothing Then patternExt = IO.Path.GetExtension(fileToDo.fn)
                '
                ' 2. Parse the pattern-string into its constitutent parts
                Dim patternSplit0 = pattern.Split({"%"c})
                Dim patternSplit As New List(Of String)
                If patternSplit0(0).Length > 0 Then patternSplit.Add(patternSplit0(0))
                For i = 1 To patternSplit0.Length - 1
                    Dim s = "%" & patternSplit0(i)
                    If Not s.StartsWith("%{") Then Console.WriteLine("ERROR: wrong pattern") : Return
                    Dim ib = s.IndexOf("}")
                    patternSplit.Add(s.Substring(0, ib + 1))
                    If ib<> s.Length - 1 Then patternSplit.Add(s.Substring(ib + 1))
                Next
                Dim patternParts As New LinkedList(Of PatternPart)

                For Each rsplit In patternSplit
                    Dim part As New PatternPart
                    part.pattern = rsplit

                    If Not rsplit.StartsWith("%") Then
                        part.generator = Function() rsplit
                        part.matcher = Function(rr)
                                           If rr.Length<rsplit.Length Then Return -1
                                           If rr.Substring(0, rsplit.Length) = rsplit Then Return rsplit.Length
                                           Return -1
                                       End Function
                        Dim prevPart = patternParts.LastOrDefault
                        If prevPart IsNot Nothing AndAlso prevPart.matcher Is Nothing Then
                            prevPart.matcher = Function(rr)
                                                   Dim i = rr.IndexOf(rsplit)
                                                   If i = -1 Then Return rr.Length
                                                   Return i
                                               End Function
                        End If
                        patternParts.AddLast(part)
                        Continue For
                    End If

                    If rsplit.StartsWith("%{fn}") Then
                        part.generator = Function(fn2, dt2, pl2) fn2
                        part.matcher = Nothing ' must be filled in by the next part
                        patternParts.AddLast(part)
                        Continue For
                    End If

                    If rsplit.StartsWith("%{place}") Then
                        part.generator = Function(fn2, dt2, pl2) pl2
                        part.matcher = Nothing ' must be filled in by the next part
                        patternParts.AddLast(part)
                        Continue For
                    End If

                    Dim escapes = {"%{datetime}", "yyyy.MM.dd - HH.mm.ss", "####.##.## - ##.##.##",
                                   "%{date}", "yyyy.MM.dd", "####.##.##",
                                   "%{time}", "HH.mm.ss", "##.##.##",
                                   "%{year}", "yyyy", "####",
                                   "%{month}", "MM", "##",
                                   "%{day}", "dd", "##",
                                   "%{hour}", "HH", "##",
                                   "%{minute}", "mm", "##",
                                   "%{second}", "ss", "##"}
                    Dim escape = "", fmt = "", islike = ""
                    For i = 0 To escapes.Length - 1 Step 3
                        If Not rsplit.StartsWith(escapes(i)) Then Continue For
                        escape = escapes(i)
                        fmt = escapes(i + 1)
                        islike = escapes(i + 2)
                        Exit For
                    Next
                    If escape = "" Then Console.WriteLine("Unrecognized {0}", rsplit) : Return
                    part.generator = Function(fn2, dt2, pl2) dt2.ToString(fmt)
                    part.matcher = Function(rr)
                                       If rr.Length<islike.Length Then Return -1
                                       If rr.Substring(0, islike.Length) Like islike Then Return islike.Length
                                       Return -1
                                   End Function
                    patternParts.AddLast(part)
                Next

                ' The last part, if it was %{fn} or %{place} will match
                ' up to the remainder of the original filename
                Dim lastPart = patternParts.Last.Value
                If lastPart.matcher Is Nothing Then lastPart.matcher = Function(rr) rr.Length

                '
                ' 3. Attempt to match the existing filename against the pattern
                Dim basefn = IO.Path.GetFileNameWithoutExtension(fileToDo.fn)
                Dim matchremainder = basefn
                Dim matchParts As New LinkedList(Of PatternPart)(patternParts)
                While matchParts.Count > 0 AndAlso matchremainder.Length > 0
                    Dim matchPart As PatternPart = matchParts.First.Value
                    Dim matchLength = matchPart.matcher(matchremainder)
                    If matchLength = -1 Then Exit While
                    matchParts.RemoveFirst()
                    If matchPart.pattern = "%{fn}" Then basefn = matchremainder.Substring(0, matchLength)
                    matchremainder = matchremainder.Substring(matchLength)
                End While

                If matchremainder.Length = 0 AndAlso matchParts.Count = 2 AndAlso matchParts(0).pattern = " - " AndAlso matchParts(1).pattern = "%{place}" Then
                    ' hack if you had pattern like "%{year} - %{fn} - %{place}" so
                    ' it will match a filename like "2012 - file.jpg" which lacks a place
                    matchParts.Clear()
                End If

                If matchParts.Count<> 0 OrElse matchremainder.Length > 0 Then
                    ' failed to do a complete match
                    basefn = IO.Path.GetFileNameWithoutExtension(fileToDo.fn)
                End If
                '
                ' 4. Figure out the new filename
                Dim newfn = IO.Path.GetDirectoryName(fileToDo.fn) & "\"
                For Each patternPart In patternParts
                    newfn &= patternPart.generator(basefn, fileToDo.localTime, fileToDo.hasGpsResult)
                Next
                If patternParts.Count > 2 AndAlso patternParts.Last.Value.pattern = "%{place}" AndAlso patternParts.Last.Previous.Value.pattern = " - " AndAlso String.IsNullOrEmpty(fileToDo.hasGpsResult) Then
                    If newfn.EndsWith(" - ") Then newfn = newfn.Substring(0, newfn.Length - 3)
                End If
                newfn &= patternExt
                If fileToDo.fn<> newfn Then
                   If IO.File.Exists(newfn) Then Console.WriteLine("Already exists - " & IO.Path.GetFileName(newfn)) : Continue While
                    Console.WriteLine(IO.Path.GetFileName(newfn))
                    IO.File.Move(fileToDo.fn, newfn)
                End If
            End If

        End While
    End Sub







    Function MetadataTimeAndGps(fn As String) As Tuple(Of DateTimeKind?, UpdateTimeFunc, GpsCoordinates)
        Using file As New IO.FileStream(fn, IO.FileMode.Open, IO.FileAccess.Read)
            file.Seek(0, SeekOrigin.End) : Dim fend = file.Position
            If fend< 8 Then Return EmptyResult
            file.Seek(0, SeekOrigin.Begin)
            Dim h1 = file.Read2byte(), h2 = file.Read2byte(), h3 = file.Read4byte()
            If h1 = &HFFD8 Then Return ExifTime(file, 0, fend) ' jpeg header
            If h3 = &H66747970 Then Return Mp4Time(file, 0, fend) ' "ftyp" prefix of mp4, mov
            Return Nothing
        End Using
    End Function





*/
