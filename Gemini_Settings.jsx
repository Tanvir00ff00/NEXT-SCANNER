try {
    var bat = new File("C:/PS_Fix/gem_settings.bat");
    if (!bat.exists) { alert("gem_settings.bat not found in C:\\PS_Fix"); }
    else if (!bat.execute()) { alert("Open C:\\PS_Fix\\gem_settings.bat by hand."); }
} catch (err) { alert("Could not open settings:\n" + err); }