// =============================================================================
// nsfonts - builds the document editor's font catalog from this machine's fonts
//
// The editor does not read Windows' fonts directly. It reads a catalog:
// AllFonts.js (which family lives in which file), font_selection.bin (how a
// missing family is matched to one that exists), the name thumbnails its font
// picker draws, and a web copy of every face. ONLYOFFICE's own allfontsgen
// makes these; its engine ships inside graphics.dll as CApplicationFontsWorker,
// and this is the same program, reduced to what NextScan needs.
//
// Run on the operator's machine, never at build time: the catalog is a list of
// the fonts installed here -- SutonnyMJ and the rest of the shop's Bijoy fonts
// included -- and no font file is ever shipped by us.
//
//   nsfonts <output folder> [extra font folder;...]
//
// Writes into <output folder>:
//   AllFonts.js           the catalog, each face by its path on disk
//   font_selection.bin    the matcher, also read by x2t
//   images\               the picker's name thumbnails
//
// Derived from ONLYOFFICE core (DesktopEditor/AllFontsGen/main.cpp, v9.4.0),
// AGPL-3.0. The class declaration below is theirs, copied field for field: the
// layout has to match the DLL's exactly.
// =============================================================================
#include <string>
#include <vector>
#include <cstdio>
#include <windows.h>

namespace NSFonts { class IApplicationFonts; }

class CApplicationFontsWorkerBreaker
{
public:
    virtual bool IsFontsWorkerRunned() { return true; }
};

class CApplicationFontsWorker_private;
class __declspec(dllimport) CApplicationFontsWorker
{
public:
    bool                        m_bIsUseSystemFonts;
    bool                        m_bIsUseSystemUserFonts;
    std::vector<std::wstring>   m_arAdditionalFolders;
    std::wstring                m_sDirectory;
    bool                        m_bIsUseOpenType;
    bool                        m_bIsUseAllVersions;
    bool                        m_bIsNeedThumbnails;
    bool                        m_bIsRemoveOldThumbnails;
    bool                        m_bSeparateThumbnails;
    std::vector<double>         m_arThumbnailsScales;
    bool                        m_bIsGenerateThumbnailsEA;
    std::wstring                m_sThumbnailsDirectory;
    std::wstring                m_sAllFontsJSPath;
    std::wstring                m_sWebAllFontsJSPath;
    std::wstring                m_sWebFontsDirectory;
    bool                        m_bIsCleanDirectory;
private:
    CApplicationFontsWorker_private* m_pInternal;
public:
    CApplicationFontsWorker();
    ~CApplicationFontsWorker();
    NSFonts::IApplicationFonts* Check();
    void CheckThumbnails();
};

static std::wstring Join(const std::wstring& dir, const wchar_t* name)
{
    if (dir.empty()) return name;
    wchar_t last = dir[dir.size() - 1];
    return (last == L'\\' || last == L'/') ? dir + name : dir + L"\\" + name;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        fwprintf(stderr, L"usage: nsfonts <output folder> [extra font folder;...]\n");
        return 2;
    }

    std::wstring out = argv[1];
    std::wstring images = Join(out, L"images");
    CreateDirectoryW(out.c_str(), NULL);
    CreateDirectoryW(images.c_str(), NULL);

    CApplicationFontsWorker worker;

    // Both the machine's fonts and the ones installed for this user only:
    // Windows 10 and later put a font installed without admin rights under
    // the profile, and a shop's Bijoy fonts are very often installed that way.
    worker.m_bIsUseSystemFonts = true;
    worker.m_bIsUseSystemUserFonts = true;

    if (argc > 2)
    {
        std::wstring list = argv[2];
        size_t from = 0;
        while (from <= list.size())
        {
            size_t to = list.find(L';', from);
            if (to == std::wstring::npos) to = list.size();
            if (to > from) worker.m_arAdditionalFolders.push_back(list.substr(from, to - from));
            from = to + 1;
        }
    }

    worker.m_sDirectory = out;                 // font_selection.bin and fonts.log
    worker.m_bIsCleanDirectory = false;

    worker.m_sThumbnailsDirectory = images;
    worker.m_bIsNeedThumbnails = true;
    worker.m_bIsRemoveOldThumbnails = true;
    worker.m_bSeparateThumbnails = true;

    // Only the desktop catalog, which names each face by its path on disk.
    // allfontsgen can also write a web catalog and a web copy of every face,
    // and on this machine that was 1,110 files and 622 MB. Measured, the web
    // catalog is the desktop one with the paths replaced by numbers, and each
    // web copy is the original with its first 32 bytes XORed with a fixed
    // key -- so NextScan.Docs makes both as the editor asks for them, and no
    // font is ever copied.
    worker.m_sAllFontsJSPath = Join(out, L"AllFonts.js");
    worker.m_bIsUseOpenType = true;

    NSFonts::IApplicationFonts* result = worker.Check();
    if (result == NULL)
    {
        fwprintf(stderr, L"nsfonts: the font worker returned nothing\n");
        return 1;
    }
    worker.CheckThumbnails();

    // The interface is reference counted and released by the process ending.
    wprintf(L"ok\n");
    return 0;
}
