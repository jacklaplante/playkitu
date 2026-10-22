#pragma once
#include <windows.h>

// Read the dedicated developer-trusted handoff from the Windows environment.
// CRT getenv can retain an old snapshot after managed SetEnvironmentVariable.
// Copy before any worker or SDK owner is created; never consult AWS_REGION.
inline bool sbrec_copy_region(char (&region)[32]) noexcept {
    const DWORD length=GetEnvironmentVariableA("PLAYREEL_AWS_REGION",region,sizeof(region));
    if(length<6 || length>=sizeof(region) || region[2]!='-' || region[length-2]!='-' ||
       region[length-1]<'0' || region[length-1]>'9')return false;
    for(DWORD i=0;i<length-2;i++) {
        if(i==2)continue;
        if(region[i]<'a' || region[i]>'z')return false;
    }
    return true;
}
