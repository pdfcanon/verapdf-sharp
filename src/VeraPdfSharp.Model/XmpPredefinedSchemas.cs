using System.Text.RegularExpressions;
using System.Xml.Linq;
using VeraPdfSharp.Core;

namespace VeraPdfSharp.Model;

internal sealed partial class PdfModelBuilder
{
    // XMP namespace constants
    private static readonly XNamespace NsRdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace NsPdfaExtension = "http://www.aiim.org/pdfa/ns/extension/";
    private static readonly XNamespace NsPdfaSchema = "http://www.aiim.org/pdfa/ns/schema#";
    private static readonly XNamespace NsPdfaProperty = "http://www.aiim.org/pdfa/ns/property#";
    private static readonly XNamespace NsPdfaType = "http://www.aiim.org/pdfa/ns/type#";
    private static readonly XNamespace NsPdfaField = "http://www.aiim.org/pdfa/ns/field#";

    /// <summary>
    /// Known XMP simple type names (used for isValueTypeDefined checks).
    /// </summary>
    private static readonly HashSet<string> KnownXmpTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Boolean", "Integer", "Real", "Text", "Date", "URI", "URL", "XPath",
        "Locale", "MIMEType", "ProperName", "AgentName", "Rational",
        "GPSCoordinate", "RenditionClass",
        // Container types
        "bag Text", "seq Text", "bag ProperName", "seq ProperName",
        "bag Date", "seq Date", "bag Locale", "lang Alt",
        "seq Integer", "seq Rational", "seq Real",
        // Structured types (common)
        "Dimensions", "Thumbnail", "ResourceEvent", "ResourceRef", "Version", "Job",
        "Flash", "OECF/SFR", "CFAPattern", "DeviceSettings",
        // Structured types (PDF/A-2/3)
        "Font", "Colorant", "BeatSpliceStretch", "Marker", "Media",
        "ProjectLink", "ResampleStretch", "Time", "Timecode", "TimeScaleStretch",
    };

    /// <summary>
    /// XMP structured types — value must be a struct node (rdf:Description or rdf:parseType="Resource").
    /// </summary>
    private static readonly HashSet<string> StructuredXmpTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dimensions", "Thumbnail", "ResourceEvent", "ResourceRef", "Version", "Job",
        "Flash", "OECF/SFR", "CFAPattern", "DeviceSettings",
        "Font", "Colorant", "BeatSpliceStretch", "Marker", "Media",
        "ProjectLink", "ResampleStretch", "Time", "Timecode", "TimeScaleStretch",
    };

    // Predefined properties for XMP 2004 (PDF/A-1): (namespaceURI, localName) → type
    private static readonly Dictionary<(string Ns, string Name), string> Predefined2004 = BuildPredefined2004();
    private static readonly Dictionary<(string Ns, string Name), string> Predefined2005 = BuildPredefined2005();

    private static Dictionary<(string Ns, string Name), string> BuildCommonProperties()
    {
        var d = new Dictionary<(string, string), string>();

        // PDF/A Identification — http://www.aiim.org/pdfa/ns/id/
        const string nsPdfaId = "http://www.aiim.org/pdfa/ns/id/";
        d[(nsPdfaId, "part")] = "Integer";
        d[(nsPdfaId, "amd")] = "Text";
        d[(nsPdfaId, "conformance")] = "Text";

        // Dublin Core — http://purl.org/dc/elements/1.1/
        const string nsDc = "http://purl.org/dc/elements/1.1/";
        d[(nsDc, "contributor")] = "bag ProperName";
        d[(nsDc, "coverage")] = "Text";
        d[(nsDc, "creator")] = "seq ProperName";
        d[(nsDc, "date")] = "seq Date";
        d[(nsDc, "description")] = "lang Alt";
        d[(nsDc, "format")] = "MIMEType";
        d[(nsDc, "identifier")] = "Text";
        d[(nsDc, "language")] = "bag Locale";
        d[(nsDc, "publisher")] = "bag ProperName";
        d[(nsDc, "relation")] = "bag Text";
        d[(nsDc, "rights")] = "lang Alt";
        d[(nsDc, "source")] = "Text";
        d[(nsDc, "subject")] = "bag Text";
        d[(nsDc, "title")] = "lang Alt";
        d[(nsDc, "type")] = "bag Text";

        // XMP Basic — http://ns.adobe.com/xap/1.0/
        const string nsXmp = "http://ns.adobe.com/xap/1.0/";
        d[(nsXmp, "Advisory")] = "bag Text";
        d[(nsXmp, "BaseURL")] = "URL";
        d[(nsXmp, "CreateDate")] = "Date";
        d[(nsXmp, "CreatorTool")] = "AgentName";
        d[(nsXmp, "Identifier")] = "bag Text";
        d[(nsXmp, "MetadataDate")] = "Date";
        d[(nsXmp, "ModifyDate")] = "Date";
        d[(nsXmp, "Nickname")] = "Text";
        d[(nsXmp, "Thumbnails")] = "alt Thumbnail";

        // XMP Rights — http://ns.adobe.com/xap/1.0/rights/
        const string nsXmpRights = "http://ns.adobe.com/xap/1.0/rights/";
        d[(nsXmpRights, "Certificate")] = "URL";
        d[(nsXmpRights, "Marked")] = "Boolean";
        d[(nsXmpRights, "Owner")] = "bag ProperName";
        d[(nsXmpRights, "UsageTerms")] = "lang Alt";
        d[(nsXmpRights, "WebStatement")] = "URL";

        // XMP Media Management — http://ns.adobe.com/xap/1.0/mm/
        const string nsXmpMM = "http://ns.adobe.com/xap/1.0/mm/";
        d[(nsXmpMM, "DerivedFrom")] = "ResourceRef";
        d[(nsXmpMM, "DocumentID")] = "URI";
        d[(nsXmpMM, "History")] = "seq ResourceEvent";
        d[(nsXmpMM, "InstanceID")] = "URI";
        d[(nsXmpMM, "ManagedFrom")] = "ResourceRef";
        d[(nsXmpMM, "Manager")] = "AgentName";
        d[(nsXmpMM, "ManageTo")] = "URI";
        d[(nsXmpMM, "ManageUI")] = "URI";
        d[(nsXmpMM, "ManagerVariant")] = "Text";
        d[(nsXmpMM, "RenditionClass")] = "RenditionClass";
        d[(nsXmpMM, "RenditionParams")] = "Text";
        d[(nsXmpMM, "VersionID")] = "Text";
        d[(nsXmpMM, "Versions")] = "seq Version";
        d[(nsXmpMM, "LastURL")] = "URL";
        d[(nsXmpMM, "RenditionOf")] = "ResourceRef";
        d[(nsXmpMM, "SaveID")] = "Integer";

        // XMP Basic Job — http://ns.adobe.com/xap/1.0/bj/
        d[("http://ns.adobe.com/xap/1.0/bj/", "JobRef")] = "bag Job";

        // XMP Paged Text — http://ns.adobe.com/xap/1.0/t/pg/
        const string nsXmpTPg = "http://ns.adobe.com/xap/1.0/t/pg/";
        d[(nsXmpTPg, "MaxPageSize")] = "Dimensions";
        d[(nsXmpTPg, "NPages")] = "Integer";

        // Adobe PDF — http://ns.adobe.com/pdf/1.3/
        const string nsPdf = "http://ns.adobe.com/pdf/1.3/";
        d[(nsPdf, "Keywords")] = "Text";
        d[(nsPdf, "PDFVersion")] = "Text";
        d[(nsPdf, "Producer")] = "AgentName";

        // Photoshop (common) — http://ns.adobe.com/photoshop/1.0/
        const string nsPs = "http://ns.adobe.com/photoshop/1.0/";
        d[(nsPs, "AuthorsPosition")] = "Text";
        d[(nsPs, "CaptionWriter")] = "ProperName";
        d[(nsPs, "Category")] = "Text";
        d[(nsPs, "City")] = "Text";
        d[(nsPs, "Country")] = "Text";
        d[(nsPs, "Credit")] = "Text";
        d[(nsPs, "DateCreated")] = "Date";
        d[(nsPs, "Headline")] = "Text";
        d[(nsPs, "Instructions")] = "Text";
        d[(nsPs, "Source")] = "Text";
        d[(nsPs, "State")] = "Text";
        d[(nsPs, "TransmissionReference")] = "Text";
        d[(nsPs, "Urgency")] = "Integer";

        // TIFF — http://ns.adobe.com/tiff/1.0/
        const string nsTiff = "http://ns.adobe.com/tiff/1.0/";
        d[(nsTiff, "ImageWidth")] = "Integer";
        d[(nsTiff, "ImageLength")] = "Integer";
        d[(nsTiff, "BitsPerSample")] = "seq Integer";
        d[(nsTiff, "Compression")] = "Integer";
        d[(nsTiff, "PhotometricInterpretation")] = "Integer";
        d[(nsTiff, "Orientation")] = "Integer";
        d[(nsTiff, "SamplesPerPixel")] = "Integer";
        d[(nsTiff, "PlanarConfiguration")] = "Integer";
        d[(nsTiff, "YCbCrSubSampling")] = "seq Integer";
        d[(nsTiff, "YCbCrPositioning")] = "Integer";
        d[(nsTiff, "XResolution")] = "Rational";
        d[(nsTiff, "YResolution")] = "Rational";
        d[(nsTiff, "ResolutionUnit")] = "Integer";
        d[(nsTiff, "TransferFunction")] = "seq Integer";
        d[(nsTiff, "WhitePoint")] = "seq Rational";
        d[(nsTiff, "PrimaryChromaticities")] = "seq Rational";
        d[(nsTiff, "YCbCrCoefficients")] = "seq Rational";
        d[(nsTiff, "ReferenceBlackWhite")] = "seq Rational";
        d[(nsTiff, "DateTime")] = "Date";
        d[(nsTiff, "ImageDescription")] = "lang Alt";
        d[(nsTiff, "Make")] = "ProperName";
        d[(nsTiff, "Model")] = "ProperName";
        d[(nsTiff, "Software")] = "AgentName";
        d[(nsTiff, "Artist")] = "ProperName";
        d[(nsTiff, "Copyright")] = "lang Alt";

        // EXIF (common) — http://ns.adobe.com/exif/1.0/
        const string nsExif = "http://ns.adobe.com/exif/1.0/";
        d[(nsExif, "ExposureProgram")] = "Integer";
        d[(nsExif, "SpectralSensitivity")] = "Text";
        d[(nsExif, "ISOSpeedRatings")] = "seq Integer";
        d[(nsExif, "OECF")] = "OECF/SFR";
        d[(nsExif, "CompressedBitsPerPixel")] = "Rational";
        d[(nsExif, "ShutterSpeedValue")] = "Rational";
        d[(nsExif, "ApertureValue")] = "Rational";
        d[(nsExif, "BrightnessValue")] = "Rational";
        d[(nsExif, "ExposureBiasValue")] = "Rational";
        d[(nsExif, "MaxApertureValue")] = "Rational";
        d[(nsExif, "SubjectDistance")] = "Rational";
        d[(nsExif, "MeteringMode")] = "Integer";
        d[(nsExif, "LightSource")] = "Integer";
        d[(nsExif, "Flash")] = "Flash";
        d[(nsExif, "FocalLength")] = "Rational";
        d[(nsExif, "SubjectArea")] = "seq Integer";
        d[(nsExif, "FlashEnergy")] = "Rational";
        d[(nsExif, "SpatialFrequencyResponse")] = "OECF/SFR";
        d[(nsExif, "FocalPlaneXResolution")] = "Rational";
        d[(nsExif, "FocalPlaneYResolution")] = "Rational";
        d[(nsExif, "FocalPlaneResolutionUnit")] = "Integer";
        d[(nsExif, "SubjectLocation")] = "seq Integer";
        d[(nsExif, "ExposureIndex")] = "Rational";
        d[(nsExif, "SensingMethod")] = "Integer";
        d[(nsExif, "FileSource")] = "Integer";
        d[(nsExif, "SceneType")] = "Integer";
        d[(nsExif, "CFAPattern")] = "CFAPattern";
        d[(nsExif, "CustomRendered")] = "Integer";
        d[(nsExif, "ExposureMode")] = "Integer";
        d[(nsExif, "WhiteBalance")] = "Integer";
        d[(nsExif, "DigitalZoomRatio")] = "Rational";
        d[(nsExif, "FocalLengthIn35mmFilm")] = "Integer";
        d[(nsExif, "SceneCaptureType")] = "Integer";
        d[(nsExif, "GainControl")] = "Integer";
        d[(nsExif, "Contrast")] = "Integer";
        d[(nsExif, "Saturation")] = "Integer";
        d[(nsExif, "Sharpness")] = "Integer";
        d[(nsExif, "SubjectDistanceRange")] = "Integer";
        d[(nsExif, "ImageUniqueID")] = "Text";
        d[(nsExif, "DeviceSettingDescription")] = "DeviceSettings";
        d[(nsExif, "GPSVersionID")] = "Text";
        d[(nsExif, "GPSLatitude")] = "GPSCoordinate";
        d[(nsExif, "GPSLongitude")] = "GPSCoordinate";
        d[(nsExif, "GPSAltitudeRef")] = "Integer";
        d[(nsExif, "GPSAltitude")] = "Rational";
        d[(nsExif, "GPSTimeStamp")] = "Date";
        d[(nsExif, "GPSSatellites")] = "Text";
        d[(nsExif, "GPSStatus")] = "Text";
        d[(nsExif, "GPSDOP")] = "Rational";
        d[(nsExif, "GPSSpeedRef")] = "Text";
        d[(nsExif, "GPSSpeed")] = "Rational";
        d[(nsExif, "GPSTrackRef")] = "Text";
        d[(nsExif, "GPSTrack")] = "Rational";
        d[(nsExif, "GPSImgDirectionRef")] = "Text";
        d[(nsExif, "GPSImgDirection")] = "Rational";
        d[(nsExif, "GPSMapDatum")] = "Text";
        d[(nsExif, "GPSDestLatitude")] = "GPSCoordinate";
        d[(nsExif, "GPSDestLongitude")] = "GPSCoordinate";
        d[(nsExif, "GPSDestBearingRef")] = "Text";
        d[(nsExif, "GPSDestBearing")] = "Rational";
        d[(nsExif, "GPSDestDistanceRef")] = "Text";
        d[(nsExif, "GPSDestDistance")] = "Rational";
        d[(nsExif, "GPSProcessingMethod")] = "Text";
        d[(nsExif, "GPSAreaInformation")] = "Text";
        d[(nsExif, "GPSDifferential")] = "Integer";
        d[(nsExif, "GPSMeasureMode")] = "Integer"; // Integer in 2004, changed to Text in 2005
        d[(nsExif, "ExposureTime")] = "Rational";
        d[(nsExif, "FNumber")] = "Rational";
        d[(nsExif, "PixelXDimension")] = "Integer";
        d[(nsExif, "PixelYDimension")] = "Integer";
        d[(nsExif, "ComponentsConfiguration")] = "seq Integer";
        d[(nsExif, "UserComment")] = "lang Alt";
        d[(nsExif, "RelatedSoundFile")] = "Text";
        d[(nsExif, "DateTimeOriginal")] = "Date";
        d[(nsExif, "DateTimeDigitized")] = "Date";
        d[(nsExif, "ColorSpace")] = "Integer";

        return d;
    }

    private static Dictionary<(string Ns, string Name), string> BuildPredefined2004()
    {
        var d = BuildCommonProperties();

        // PDF/A-1 specific
        const string nsPs = "http://ns.adobe.com/photoshop/1.0/";
        d[(nsPs, "SupplementalCategories")] = "Text"; // Text in 2004, bag Text in 2005

        const string nsExif = "http://ns.adobe.com/exif/1.0/";
        d[(nsExif, "MakerNote")] = "Text";
        d[(nsExif, "ExifVersion")] = "Text";
        d[(nsExif, "FlashpixVersion")] = "Text";

        return d;
    }

    private static Dictionary<(string Ns, string Name), string> BuildPredefined2005()
    {
        var d = BuildCommonProperties();

        // PDF/A-2/3 specific overrides
        const string nsPdfaId = "http://www.aiim.org/pdfa/ns/id/";
        d[(nsPdfaId, "corr")] = "Text";

        const string nsXmp = "http://ns.adobe.com/xap/1.0/";
        d[(nsXmp, "Label")] = "Text";
        d[(nsXmp, "Rating")] = "Real";

        const string nsXmpTPg = "http://ns.adobe.com/xap/1.0/t/pg/";
        d[(nsXmpTPg, "Fonts")] = "bag Font";
        d[(nsXmpTPg, "Colorants")] = "seq Colorant";
        d[(nsXmpTPg, "PlateNames")] = "seq Text";

        const string nsPs = "http://ns.adobe.com/photoshop/1.0/";
        d[(nsPs, "SupplementalCategories")] = "bag Text"; // bag Text in 2005

        const string nsExif = "http://ns.adobe.com/exif/1.0/";
        d[(nsExif, "ExifVersion")] = "Text";
        d[(nsExif, "FlashpixVersion")] = "Text";
        d[(nsExif, "GPSMeasureMode")] = "Text"; // changed from Integer to Text in 2005

        // Auxiliary EXIF — http://ns.adobe.com/exif/1.0/aux/
        const string nsAux = "http://ns.adobe.com/exif/1.0/aux/";
        d[(nsAux, "Lens")] = "Text";
        d[(nsAux, "SerialNumber")] = "Text";

        // XMP Dynamic Media — http://ns.adobe.com/xmp/1.0/DynamicMedia/
        const string nsDm = "http://ns.adobe.com/xmp/1.0/DynamicMedia/";
        d[(nsDm, "projectRef")] = "ProjectLink";
        d[(nsDm, "videoFrameRate")] = "Text";
        d[(nsDm, "videoFrameSize")] = "Dimensions";
        d[(nsDm, "videoPixelAspectRatio")] = "Rational";
        d[(nsDm, "videoAlphaUnityIsTransparent")] = "Boolean";
        d[(nsDm, "videoAlphaPremultipleColor")] = "Colorant";
        d[(nsDm, "videoCompressor")] = "Text";
        d[(nsDm, "audioSampleRate")] = "Integer";
        d[(nsDm, "audioCompressor")] = "Text";
        d[(nsDm, "speakerPlacement")] = "Text";
        d[(nsDm, "fileDataRate")] = "Rational";
        d[(nsDm, "tapeName")] = "Text";
        d[(nsDm, "altTapeName")] = "Text";
        d[(nsDm, "startTimecode")] = "Timecode";
        d[(nsDm, "altTimecode")] = "Timecode";
        d[(nsDm, "duration")] = "Time";
        d[(nsDm, "scene")] = "Text";
        d[(nsDm, "shotName")] = "Text";
        d[(nsDm, "shotDate")] = "Date";
        d[(nsDm, "shotLocation")] = "Text";
        d[(nsDm, "logComment")] = "Text";
        d[(nsDm, "markers")] = "seq Marker";
        d[(nsDm, "contributedMedia")] = "bag Media";
        d[(nsDm, "absPeakAudioFilePath")] = "URI";
        d[(nsDm, "relativePeakAudioFilePath")] = "URI";
        d[(nsDm, "videoModDate")] = "Date";
        d[(nsDm, "audioModDate")] = "Date";
        d[(nsDm, "metadataModDate")] = "Date";
        d[(nsDm, "artist")] = "Text";
        d[(nsDm, "album")] = "Text";
        d[(nsDm, "trackNumber")] = "Integer";
        d[(nsDm, "genre")] = "Text";
        d[(nsDm, "copyright")] = "Text";
        d[(nsDm, "releaseDate")] = "Date";
        d[(nsDm, "composer")] = "Text";
        d[(nsDm, "engineer")] = "Text";
        d[(nsDm, "tempo")] = "Real";
        d[(nsDm, "instrument")] = "Text";
        d[(nsDm, "introTime")] = "Time";
        d[(nsDm, "outCue")] = "Time";
        d[(nsDm, "relativeTimestamp")] = "Time";
        d[(nsDm, "loop")] = "Boolean";
        d[(nsDm, "numberOfBeats")] = "Real";
        d[(nsDm, "timeScaleParams")] = "TimeScaleStretch";
        d[(nsDm, "resampleParams")] = "ResampleStretch";
        d[(nsDm, "beatSpliceParams")] = "BeatSpliceStretch";
        d[(nsDm, "videoPixelDepth")] = "Text";
        d[(nsDm, "videoColorSpace")] = "Text";
        d[(nsDm, "videoAlphaMode")] = "Text";
        d[(nsDm, "videoFieldOrder")] = "Text";
        d[(nsDm, "pullDown")] = "Text";
        d[(nsDm, "audioSampleType")] = "Text";
        d[(nsDm, "audioChannelType")] = "Text";
        d[(nsDm, "key")] = "Text";
        d[(nsDm, "stretchMode")] = "Text";
        d[(nsDm, "timeSignature")] = "Text";
        d[(nsDm, "scaleType")] = "Text";

        // Camera Raw — http://ns.adobe.com/camera-raw-settings/1.0/
        const string nsCr = "http://ns.adobe.com/camera-raw-settings/1.0/";
        d[(nsCr, "AutoBrightness")] = "Boolean";
        d[(nsCr, "AutoContrast")] = "Boolean";
        d[(nsCr, "AutoExposure")] = "Boolean";
        d[(nsCr, "AutoShadows")] = "Boolean";
        d[(nsCr, "BlueHue")] = "Integer";
        d[(nsCr, "BlueSaturation")] = "Integer";
        d[(nsCr, "Brightness")] = "Integer";
        d[(nsCr, "CameraProfile")] = "Text";
        d[(nsCr, "ChromaticAberrationB")] = "Integer";
        d[(nsCr, "ChromaticAberrationR")] = "Integer";
        d[(nsCr, "ColorNoiseReduction")] = "Integer";
        d[(nsCr, "Contrast")] = "Integer";
        d[(nsCr, "CropTop")] = "Real";
        d[(nsCr, "CropLeft")] = "Real";
        d[(nsCr, "CropBottom")] = "Real";
        d[(nsCr, "CropRight")] = "Real";
        d[(nsCr, "CropAngle")] = "Real";
        d[(nsCr, "CropWidth")] = "Real";
        d[(nsCr, "CropHeight")] = "Real";
        d[(nsCr, "CropUnits")] = "Integer";
        d[(nsCr, "Exposure")] = "Real";
        d[(nsCr, "GreenHue")] = "Integer";
        d[(nsCr, "GreenSaturation")] = "Integer";
        d[(nsCr, "HasCrop")] = "Boolean";
        d[(nsCr, "HasSettings")] = "Boolean";
        d[(nsCr, "LuminanceSmoothing")] = "Integer";
        d[(nsCr, "RawFileName")] = "Text";
        d[(nsCr, "RedHue")] = "Integer";
        d[(nsCr, "RedSaturation")] = "Integer";
        d[(nsCr, "Saturation")] = "Integer";
        d[(nsCr, "Shadows")] = "Integer";
        d[(nsCr, "ShadowTint")] = "Integer";
        d[(nsCr, "Sharpness")] = "Integer";
        d[(nsCr, "Temperature")] = "Integer";
        d[(nsCr, "Tint")] = "Integer";
        d[(nsCr, "ToneCurveName")] = "Text";
        d[(nsCr, "Version")] = "Text";
        d[(nsCr, "VignetteAmount")] = "Integer";
        d[(nsCr, "VignetteMidpoint")] = "Integer";
        d[(nsCr, "WhiteBalance")] = "Text";
        d[(nsCr, "ToneCurve")] = "seq Text";

        return d;
    }

    // ---- XMP property extraction and model creation ----

    /// <summary>
    /// Collects all XMP properties from an XDocument, returning them as
    /// (namespaceURI, prefix, localName, isLangAlt, xNode) tuples.
    /// </summary>
    private static List<(string Ns, string Prefix, string LocalName, bool IsLangAlt, XObject Node)> CollectXmpProperties(XDocument document)
    {
        var result = new List<(string, string, string, bool, XObject)>();
        var rdfDescription = NsRdf + "Description";
        var rdfAlt = NsRdf + "Alt";
        var xmlLang = XNamespace.Xml + "lang";

        // XMP namespaces that are structural/internal — skip these
        var skipNamespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
            "http://www.w3.org/XML/1998/namespace",
            "http://www.w3.org/2000/xmlns/",
            "http://www.aiim.org/pdfa/ns/extension/",
        };

        // Only iterate top-level rdf:Description elements (direct children of rdf:RDF)
        // to avoid picking up properties from nested structured values
        var rdfRDF = NsRdf + "RDF";
        foreach (var desc in document.Descendants(rdfRDF).Elements(rdfDescription))
        {
            // Properties can be attributes on rdf:Description
            foreach (var attr in desc.Attributes())
            {
                if (attr.Name.Namespace == XNamespace.None || skipNamespaces.Contains(attr.Name.NamespaceName))
                    continue;
                if (attr.Name == rdfDescription || attr.Name.LocalName == "about")
                    continue;
                result.Add((attr.Name.NamespaceName, attr.Parent?.GetPrefixOfNamespace(attr.Name.Namespace) ?? "", attr.Name.LocalName, false, attr));
            }

            // Properties can be child elements of rdf:Description
            foreach (var elem in desc.Elements())
            {
                if (skipNamespaces.Contains(elem.Name.NamespaceName))
                    continue;
                // Skip extension schemas container — handled separately
                if (elem.Name == NsPdfaExtension + "schemas")
                    continue;

                // Check if this is a lang alt
                bool isLangAlt = elem.Elements(rdfAlt).Any();

                var prefix = elem.GetPrefixOfNamespace(elem.Name.Namespace) ?? "";
                result.Add((elem.Name.NamespaceName, prefix, elem.Name.LocalName, isLangAlt, elem));
            }
        }

        return result;
    }

    /// <summary>
    /// Simplifies an XMP type string following veraPDF conventions:
    /// lowercase, strip "open "/"closed " and "choice " prefixes,
    /// bare "bag"/"seq"/"alt" becomes "bag text"/"seq text"/"alt text".
    /// </summary>
    private static string SimplifyType(string type)
    {
        var s = type.ToLowerInvariant().Trim();
        s = s.Replace("open ", "", StringComparison.Ordinal)
             .Replace("closed ", "", StringComparison.Ordinal)
             .Replace("choice of ", "", StringComparison.Ordinal)
             .Replace("choice ", "", StringComparison.Ordinal)
             .Trim();
        if (s.Length == 0) return "text";
        // Bare aggregate keyword → append " text"
        if (s is "bag" or "seq" or "alt") return s + " text";
        if (s.EndsWith(" lang alt", StringComparison.Ordinal)) return s;
        return s;
    }

    /// <summary>
    /// Validates an XMP property value against its expected type string.
    /// Returns true if correct, false if incorrect.
    /// </summary>
    private static bool ValidateValueType(XObject node, string expectedType)
    {
        var simplified = SimplifyType(expectedType);

        // "any" always passes
        if (simplified == "any") return true;

        // Lang alt
        if (simplified.EndsWith(" lang alt", StringComparison.Ordinal) || simplified == "lang alt")
        {
            return node is XElement el && el.Elements(NsRdf + "Alt").Any();
        }

        // Array types: bag X, seq X, alt X
        if (simplified.StartsWith("bag ", StringComparison.Ordinal))
            return ValidateArray(node, "bag");
        if (simplified.StartsWith("seq ", StringComparison.Ordinal))
            return ValidateArray(node, "seq");
        if (simplified.StartsWith("alt ", StringComparison.Ordinal))
            return ValidateArray(node, "alt");

        // Simple or structured type
        return ValidateSimpleOrStructuredType(node, simplified);
    }

    private static bool ValidateArray(XObject node, string kind)
    {
        if (node is not XElement el) return false;

        var hasBag = el.Elements(NsRdf + "Bag").Any();
        var hasSeq = el.Elements(NsRdf + "Seq").Any();
        var hasAlt = el.Elements(NsRdf + "Alt").Any();

        return kind switch
        {
            "bag" => hasBag && !hasSeq && !hasAlt,
            "seq" => hasSeq && !hasAlt,
            "alt" => hasAlt,
            _ => false,
        };
    }

    private static bool ValidateSimpleOrStructuredType(XObject node, string type)
    {
        // Attribute nodes are always simple
        if (node is XAttribute attr)
        {
            return IsSimpleValueCorrect(attr.Value, type);
        }

        if (node is not XElement el) return false;

        // Check if this is a "simple" element (text content, no rdf:* children)
        bool hasRdfChildren = el.Elements(NsRdf + "Bag").Any()
                           || el.Elements(NsRdf + "Seq").Any()
                           || el.Elements(NsRdf + "Alt").Any()
                           || el.Elements(NsRdf + "Description").Any();

        bool isStruct = el.Elements(NsRdf + "Description").Any()
                     || (string.Equals(el.Attribute(NsRdf + "parseType")?.Value, "Resource", StringComparison.Ordinal));

        // If it's a known structured type, validate as struct
        if (StructuredXmpTypes.Contains(type) || type.Contains('.', StringComparison.Ordinal))
        {
            // Structured type: must be a struct node
            return isStruct;
        }

        // For simple types, verify value is correct
        if (!hasRdfChildren)
        {
            return IsSimpleValueCorrect(el.Value.Trim(), type);
        }

        // Element has rdf children but expects simple type → incorrect
        return false;
    }

    private static readonly Regex BooleanPattern = new(@"^(True|False)$", RegexOptions.Compiled);
    private static readonly Regex IntegerPattern = new(@"^[+-]?\d+$", RegexOptions.Compiled);
    private static readonly Regex RealPattern = new(@"^[+-]?\d+\.?\d*$|^[+-]?\d*\.?\d+$", RegexOptions.Compiled);
    private static readonly Regex MimeTypePattern = new(@"^[-\w+\.]+/[-\w+\.]+$", RegexOptions.Compiled);
    // ISO 8601: YYYY, YYYY-MM, YYYY-MM-DD, YYYY-MM-DDThh:mmTZD, YYYY-MM-DDThh:mm:ssTZD, YYYY-MM-DDThh:mm:ss.sTZD
    private static readonly Regex DatePattern = new(
        @"^\d{4}(-\d{2}(-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})?)?)?)?$",
        RegexOptions.Compiled);

    private static bool IsSimpleValueCorrect(string value, string type)
    {
        return type switch
        {
            "text" or "agentname" or "propername" or "rational" or "renditionclass" or "locale" => true,
            "boolean" => BooleanPattern.IsMatch(value),
            "integer" => IntegerPattern.IsMatch(value),
            "real" => RealPattern.IsMatch(value),
            "mimetype" => MimeTypePattern.IsMatch(value),
            "date" => DatePattern.IsMatch(value),
            "uri" or "url" => true, // Per veraPDF TWG decision, URI/URL syntax check is disabled
            "xpath" => true, // We don't have XPath.compile() readily available
            _ => true, // Unknown type — don't fail
        };
    }

    /// <summary>
    /// Parses extension schemas from an XMP document and returns a set of
    /// (namespaceURI, propertyName) pairs that are defined by extension schemas.
    /// Also returns the list of ExtensionSchema model objects for the validator.
    /// </summary>
    private static (HashSet<(string Ns, string Name)> DefinedProperties, List<IModelObject> SchemaObjects) ParseExtensionSchemas(XDocument document)
    {
        var definedProperties = new HashSet<(string, string)>();
        var schemaObjects = new List<IModelObject>();

        // Find pdfaExtension:schemas elements
        var schemasElements = document.Descendants(NsPdfaExtension + "schemas").ToList();
        if (schemasElements.Count == 0)
            return (definedProperties, schemaObjects);

        foreach (var schemasElement in schemasElements)
        {
            // Create ExtensionSchemasContainer
            var container = new GenericModelObject("ExtensionSchemasContainer");
            var containerPrefix = schemasElement.GetPrefixOfNamespace(NsPdfaExtension) ?? "";
            container.Set("prefix", containerPrefix);

            // Check if it's a valid Bag (rdf:Bag child)
            var bagElement = schemasElement.Element(NsRdf + "Bag");
            var seqElement = schemasElement.Element(NsRdf + "Seq");
            container.Set("isValidBag", bagElement is not null);

            var definitions = new List<IModelObject>();
            var schemaItems = (bagElement ?? seqElement)?.Elements(NsRdf + "li") ?? schemasElement.Elements(NsRdf + "li");

            foreach (var schemaItem in schemaItems)
            {
                // Each li is an rdf:Description or has child elements
                var schemaDesc = schemaItem.Element(NsRdf + "Description") ?? schemaItem;

                var definition = ParseExtensionSchemaDefinition(schemaDesc, definedProperties);
                definitions.Add(definition);
            }

            container.Link("ExtensionSchemaDefinitions", definitions.ToArray());
            schemaObjects.Add(container);
        }

        return (definedProperties, schemaObjects);
    }

    private static GenericModelObject ParseExtensionSchemaDefinition(XElement schemaDesc,
        HashSet<(string, string)> definedProperties)
    {
        var definition = new GenericModelObject("ExtensionSchemaDefinition",
            superTypes: new[] { "ExtensionSchemaObject", "XMPObject" });

        // Read child elements
        var namespaceURIElem = schemaDesc.Element(NsPdfaSchema + "namespaceURI");
        var prefixElem = schemaDesc.Element(NsPdfaSchema + "prefix");
        var schemaElem = schemaDesc.Element(NsPdfaSchema + "schema");
        var propertyElem = schemaDesc.Element(NsPdfaSchema + "property");
        var valueTypeElem = schemaDesc.Element(NsPdfaSchema + "valueType");

        var schemaNamespaceUri = namespaceURIElem?.Value?.Trim();

        // Set prefix properties (namespace prefix of the XML element itself)
        definition.Set("namespaceURIPrefix", namespaceURIElem is not null ? GetElementPrefix(namespaceURIElem) : null);
        definition.Set("prefixPrefix", prefixElem is not null ? GetElementPrefix(prefixElem) : null);
        definition.Set("schemaPrefix", schemaElem is not null ? GetElementPrefix(schemaElem) : null);
        definition.Set("propertyPrefix", propertyElem is not null ? GetElementPrefix(propertyElem) : null);
        definition.Set("valueTypePrefix", valueTypeElem is not null ? GetElementPrefix(valueTypeElem) : null);

        // Validation booleans
        definition.Set("isNamespaceURIValidURI", namespaceURIElem is not null && IsValidUri(namespaceURIElem.Value));
        definition.Set("isPrefixValidText", prefixElem is not null && IsValidText(prefixElem.Value));
        definition.Set("isSchemaValidText", schemaElem is not null && IsValidText(schemaElem.Value));
        definition.Set("isPropertyValidSeq", IsValidSeq(propertyElem));
        definition.Set("isValueTypeValidSeq", IsValidSeq(valueTypeElem));

        // Check for undefined fields
        var validDefinitionFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "namespaceURI", "prefix", "schema", "property", "valueType"
        };
        var (hasUndefined, undefinedList) = CheckUndefinedFields(schemaDesc, NsPdfaSchema, validDefinitionFields);
        definition.Set("containsUndefinedFields", hasUndefined);
        definition.Set("undefinedFields", undefinedList);

        // Parse properties
        var extensionProperties = new List<IModelObject>();
        if (propertyElem is not null)
        {
            var propSeq = propertyElem.Element(NsRdf + "Seq") ?? propertyElem.Element(NsRdf + "Bag") ?? propertyElem;
            foreach (var propLi in propSeq.Elements(NsRdf + "li"))
            {
                var propDesc = propLi.Element(NsRdf + "Description") ?? propLi;
                var prop = ParseExtensionSchemaProperty(propDesc);
                extensionProperties.Add(prop);

                // Register the property as defined
                var propName = propDesc.Element(NsPdfaProperty + "name")?.Value?.Trim();
                if (schemaNamespaceUri is not null && propName is not null)
                {
                    definedProperties.Add((schemaNamespaceUri, propName));
                }
            }
        }
        definition.Link("ExtensionSchemaProperties", extensionProperties.ToArray());

        // Parse value types
        var extensionValueTypes = new List<IModelObject>();
        if (valueTypeElem is not null)
        {
            var vtSeq = valueTypeElem.Element(NsRdf + "Seq") ?? valueTypeElem.Element(NsRdf + "Bag") ?? valueTypeElem;
            foreach (var vtLi in vtSeq.Elements(NsRdf + "li"))
            {
                var vtDesc = vtLi.Element(NsRdf + "Description") ?? vtLi;
                extensionValueTypes.Add(ParseExtensionSchemaValueType(vtDesc));
            }
        }
        definition.Link("ExtensionSchemaValueTypes", extensionValueTypes.ToArray());

        return definition;
    }

    private static GenericModelObject ParseExtensionSchemaProperty(XElement propDesc)
    {
        var prop = new GenericModelObject("ExtensionSchemaProperty",
            superTypes: new[] { "ExtensionSchemaObject", "XMPObject" });

        var nameElem = propDesc.Element(NsPdfaProperty + "name");
        var valueTypeElem = propDesc.Element(NsPdfaProperty + "valueType");
        var categoryElem = propDesc.Element(NsPdfaProperty + "category");
        var descriptionElem = propDesc.Element(NsPdfaProperty + "description");

        prop.Set("namePrefix", nameElem is not null ? GetElementPrefix(nameElem) : null);
        prop.Set("valueTypePrefix", valueTypeElem is not null ? GetElementPrefix(valueTypeElem) : null);
        prop.Set("categoryPrefix", categoryElem is not null ? GetElementPrefix(categoryElem) : null);
        prop.Set("descriptionPrefix", descriptionElem is not null ? GetElementPrefix(descriptionElem) : null);

        prop.Set("isNameValidText", nameElem is not null && IsValidText(nameElem.Value));
        prop.Set("isValueTypeValidText", valueTypeElem is not null && IsValidText(valueTypeElem.Value));
        prop.Set("isCategoryValidText", categoryElem is not null && IsValidText(categoryElem.Value));
        prop.Set("isDescriptionValidText", descriptionElem is not null && IsValidText(descriptionElem.Value));

        prop.Set("category", categoryElem?.Value?.Trim());

        // isValueTypeDefined: check if the value type string is a known type
        var vtStr = valueTypeElem?.Value?.Trim();
        prop.Set("isValueTypeDefined", vtStr is not null && IsKnownValueType(vtStr));

        var validPropertyFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "valueType", "category", "description"
        };
        var (hasUndefined, undefinedList) = CheckUndefinedFields(propDesc, NsPdfaProperty, validPropertyFields);
        prop.Set("containsUndefinedFields", hasUndefined);
        prop.Set("undefinedFields", undefinedList);

        return prop;
    }

    private static GenericModelObject ParseExtensionSchemaValueType(XElement vtDesc)
    {
        var vt = new GenericModelObject("ExtensionSchemaValueType",
            superTypes: new[] { "ExtensionSchemaObject", "XMPObject" });

        var typeElem = vtDesc.Element(NsPdfaType + "type");
        var nsUriElem = vtDesc.Element(NsPdfaType + "namespaceURI");
        var prefixElem = vtDesc.Element(NsPdfaType + "prefix");
        var descElem = vtDesc.Element(NsPdfaType + "description");
        var fieldElem = vtDesc.Element(NsPdfaType + "field");

        vt.Set("typePrefix", typeElem is not null ? GetElementPrefix(typeElem) : null);
        vt.Set("namespaceURIPrefix", nsUriElem is not null ? GetElementPrefix(nsUriElem) : null);
        vt.Set("prefixPrefix", prefixElem is not null ? GetElementPrefix(prefixElem) : null);
        vt.Set("descriptionPrefix", descElem is not null ? GetElementPrefix(descElem) : null);
        vt.Set("fieldPrefix", fieldElem is not null ? GetElementPrefix(fieldElem) : null);

        vt.Set("isTypeValidText", typeElem is not null && IsValidText(typeElem.Value));
        vt.Set("isNamespaceURIValidURI", nsUriElem is not null && IsValidUri(nsUriElem.Value));
        vt.Set("isPrefixValidText", prefixElem is not null && IsValidText(prefixElem.Value));
        vt.Set("isDescriptionValidText", descElem is not null && IsValidText(descElem.Value));
        vt.Set("isFieldValidSeq", IsValidSeq(fieldElem));

        var validValueTypeFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "type", "namespaceURI", "prefix", "description", "field"
        };
        var (hasUndefined, undefinedList) = CheckUndefinedFields(vtDesc, NsPdfaType, validValueTypeFields);
        vt.Set("containsUndefinedFields", hasUndefined);
        vt.Set("undefinedFields", undefinedList);

        // Parse fields
        var fields = new List<IModelObject>();
        if (fieldElem is not null)
        {
            var fieldSeq = fieldElem.Element(NsRdf + "Seq") ?? fieldElem.Element(NsRdf + "Bag") ?? fieldElem;
            foreach (var fieldLi in fieldSeq.Elements(NsRdf + "li"))
            {
                var fieldDesc = fieldLi.Element(NsRdf + "Description") ?? fieldLi;
                fields.Add(ParseExtensionSchemaField(fieldDesc));
            }
        }
        vt.Link("ExtensionSchemaFields", fields.ToArray());

        return vt;
    }

    private static GenericModelObject ParseExtensionSchemaField(XElement fieldDesc)
    {
        var field = new GenericModelObject("ExtensionSchemaField",
            superTypes: new[] { "ExtensionSchemaObject", "XMPObject" });

        var nameElem = fieldDesc.Element(NsPdfaField + "name");
        var valueTypeElem = fieldDesc.Element(NsPdfaField + "valueType");
        var descElem = fieldDesc.Element(NsPdfaField + "description");

        field.Set("namePrefix", nameElem is not null ? GetElementPrefix(nameElem) : null);
        field.Set("valueTypePrefix", valueTypeElem is not null ? GetElementPrefix(valueTypeElem) : null);
        field.Set("descriptionPrefix", descElem is not null ? GetElementPrefix(descElem) : null);

        field.Set("isNameValidText", nameElem is not null && IsValidText(nameElem.Value));
        field.Set("isValueTypeValidText", valueTypeElem is not null && IsValidText(valueTypeElem.Value));
        field.Set("isDescriptionValidText", descElem is not null && IsValidText(descElem.Value));

        var vtStr = valueTypeElem?.Value?.Trim();
        field.Set("isValueTypeDefined", vtStr is not null && IsKnownValueType(vtStr));

        var validFieldFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "valueType", "description"
        };
        var (hasUndefined, undefinedList) = CheckUndefinedFields(fieldDesc, NsPdfaField, validFieldFields);
        field.Set("containsUndefinedFields", hasUndefined);
        field.Set("undefinedFields", undefinedList);

        return field;
    }

    // ---- Helper methods ----

    private static string GetElementPrefix(XElement elem)
    {
        return elem.GetPrefixOfNamespace(elem.Name.Namespace) ?? "";
    }

    private static bool IsValidUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out _) || value.Trim().Contains(':');
    }

    private static bool IsValidText(string? value) => value is not null;

    private static bool IsValidSeq(XElement? element)
    {
        if (element is null) return true; // absent is valid (optional field)
        // Must contain rdf:Seq (ordered)
        return element.Element(NsRdf + "Seq") is not null;
    }

    private static bool IsKnownValueType(string typeStr)
    {
        if (KnownXmpTypes.Contains(typeStr)) return true;
        // Also accept prefixed types like "bag Text", "seq Integer", "lang Alt"
        if (typeStr.StartsWith("bag ", StringComparison.OrdinalIgnoreCase) ||
            typeStr.StartsWith("seq ", StringComparison.OrdinalIgnoreCase) ||
            typeStr.StartsWith("alt ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Accept any single-word type (could be defined by extension schema)
        return !string.IsNullOrWhiteSpace(typeStr);
    }

    private static (bool HasUndefined, string? UndefinedList) CheckUndefinedFields(
        XElement desc, XNamespace expectedNs, HashSet<string> validNames)
    {
        var undefined = new List<string>();
        foreach (var child in desc.Elements())
        {
            if (child.Name.Namespace == NsRdf) continue; // skip rdf:type etc.
            if (child.Name.Namespace == expectedNs && validNames.Contains(child.Name.LocalName)) continue;
            undefined.Add(child.Name.LocalName);
        }
        return (undefined.Count > 0, undefined.Count > 0 ? string.Join(", ", undefined) : null);
    }
}
