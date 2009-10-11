﻿/* Copyright (C) 2008-2009, Bit Miracle
 * http://www.bitmiracle.com
 * 
 * This software is based in part on the work of the Sam Leffler, Silicon 
 * Graphics, Inc. and contributors.
 *
 * Copyright (c) 1988-1997 Sam Leffler
 * Copyright (c) 1991-1997 Silicon Graphics, Inc.
 * For conditions of distribution and use, see the accompanying README file.
 */

/*
 * Directory Read Support Routines.
 */

using System;
using System.Collections.Generic;
using System.Text;

using BitMiracle.LibTiff.Internal;

namespace BitMiracle.LibTiff
{
    public partial class Tiff
    {
        private const UInt16 TIFFTAG_IGNORE = 0;       /* tag placeholder used below */

        private uint extractData(TiffDirEntry dir)
        {
            return (uint)(m_header.tiff_magic == TIFF_BIGENDIAN ? 
                (dir.tdir_offset >> m_typeshift[dir.tdir_type]) & m_typemask[dir.tdir_type] : 
                dir.tdir_offset & m_typemask[dir.tdir_type]);
        }

        private bool byteCountLooksBad(TiffDirectory td)
        {
            /* 
             * Assume we have wrong StripByteCount value (in case of single strip) in
             * following cases:
             *   - it is equal to zero along with StripOffset;
             *   - it is larger than file itself (in case of uncompressed image);
             *   - it is smaller than the size of the bytes per row multiplied on the
             *     number of rows.  The last case should not be checked in the case of
             *     writing new image, because we may do not know the exact strip size
             *     until the whole image will be written and directory dumped out.
             */
            return ((td.td_stripbytecount[0] == 0 && td.td_stripoffset[0] != 0) ||
                (td.td_compression == COMPRESSION_NONE && td.td_stripbytecount[0] > getFileSize() - td.td_stripoffset[0]) ||
                (m_mode == O_RDONLY && td.td_compression == COMPRESSION_NONE && td.td_stripbytecount[0] < ScanlineSize() * td.td_imagelength));
        }

        private static uint howMany8(uint x)
        {
            return ((x & 0x07) ? (x >> 3) + 1 : x >> 3);
        }

        private bool readDirectoryFailed(TiffDirEntry dir)
        {
            delete dir;
            return false;
        }

        private bool estimateStripByteCounts(TiffDirEntry dir, UInt16 dircount)
        {
            static const char module[] = "estimateStripByteCounts";

            delete m_dir.td_stripbytecount;
            m_dir.td_stripbytecount = new uint [m_dir.td_nstrips];
            if (m_dir.td_stripbytecount == null)
            {
                ErrorExt(this, m_clientdata, m_name, "No space for \"StripByteCounts\" array");
                return false;
            }

            if (m_dir.td_compression != COMPRESSION_NONE)
            {
                uint space = (uint)(sizeof(TiffHeader) + sizeof(UInt16) + (dircount * sizeof(TiffDirEntry)) + sizeof(uint));
                uint filesize = getFileSize();

                /* calculate amount of space used by indirect values */
                for (UInt16 n = 0; n < dircount; n++)
                {
                    uint cc = Tiff::DataWidth((TiffDataType)dir[n].tdir_type);
                    if (cc == 0)
                    {
                        Tiff::ErrorExt(this, m_clientdata, module, "%s: Cannot determine size of unknown tag type %d", m_name, dir[n].tdir_type);
                        return false;
                    }

                    cc = cc * dir[n].tdir_count;
                    if (cc > sizeof(uint))
                        space += cc;
                }

                space = filesize - space;
                if (m_dir.td_planarconfig == PLANARCONFIG_SEPARATE)
                    space /= m_dir.td_samplesperpixel;
                
                uint strip = 0;
                for ( ; strip < m_dir.td_nstrips; strip++)
                    m_dir.td_stripbytecount[strip] = space;
                
                /*
                 * This gross hack handles the case were the offset to
                 * the last strip is past the place where we think the strip
                 * should begin.  Since a strip of data must be contiguous,
                 * it's safe to assume that we've overestimated the amount
                 * of data in the strip and trim this number back accordingly.
                 */
                strip--;
                if ((m_dir.td_stripoffset[strip] + m_dir.td_stripbytecount[strip]) > filesize)
                    m_dir.td_stripbytecount[strip] = filesize - m_dir.td_stripoffset[strip];
            }
            else if (IsTiled()) 
            {
                uint bytespertile = TileSize();

                for (uint strip = 0; strip < m_dir.td_nstrips; strip++)
                    m_dir.td_stripbytecount[strip] = bytespertile;
            }
            else
            {
                uint rowbytes = ScanlineSize();
                uint rowsperstrip = m_dir.td_imagelength / m_dir.td_stripsperimage;
                for (uint strip = 0; strip < m_dir.td_nstrips; strip++)
                    m_dir.td_stripbytecount[strip] = rowbytes * rowsperstrip;
            }
            
            setFieldBit(FIELD_STRIPBYTECOUNTS);
            if (!fieldSet(FIELD_ROWSPERSTRIP))
                m_dir.td_rowsperstrip = m_dir.td_imagelength;

            return true;
        }

        private void missingRequired(string tagname)
        {
            static const char module[] = "missingRequired";
            Tiff::ErrorExt(this, m_clientdata, module, "%s: TIFF directory is missing required \"%s\" field", m_name, tagname);
        }

        private int fetchFailed(TiffDirEntry dir)
        {
            Tiff::ErrorExt(this, m_clientdata, m_name, "Error fetching data for field \"%s\"", FieldWithTag(dir.tdir_tag).field_name);
            return 0;
        }

        private static int readDirectoryFind(TiffDirEntry dir, UInt16 dircount, UInt16 tagid)
        {
            for (UInt16 n = 0; n < dircount; n++)
            {
                if (dir[n].tdir_tag == tagid)
                    return n;
            }

            return -1;
        }

        /*
        * Check the directory offset against the list of already seen directory
        * offsets. This is a trick to prevent IFD looping. The one can create TIFF
        * file with looped directory pointers. We will maintain a list of already
        * seen directories and check every IFD offset against that list.
        */
        private bool checkDirOffset(uint diroff)
        {
            if (diroff == 0)
            {
                /* no more directories */
                return false;
            }

            for (UInt16 n = 0; n < m_dirnumber && m_dirlist != null; n++)
            {
                if (m_dirlist[n] == diroff)
                    return false;
            }

            m_dirnumber++;

            if (m_dirnumber > m_dirlistsize)
            {
                /*
                * XXX: Reduce memory allocation granularity of the dirlist array.
                */
                uint* new_dirlist = Realloc(m_dirlist, m_dirnumber - 1, 2 * m_dirnumber);
                if (new_dirlist == null)
                    return false;

                m_dirlistsize = 2 * m_dirnumber;
                m_dirlist = new_dirlist;
            }

            m_dirlist[m_dirnumber - 1] = diroff;
            return true;
        }
        
        /*
        * Read IFD structure from the specified offset. If the pointer to
        * nextdiroff variable has been specified, read it too. Function returns a
        * number of fields in the directory or 0 if failed.
        */
        private UInt16 fetchDirectory(uint diroff, out TiffDirEntry[] pdir, out uint nextdiroff)
        {
            static const char module[] = "fetchDirectory";

            m_diroff = diroff;
            nextdiroff = 0;

            UInt16 dircount;
            TiffDirEntry* dir = null;
            if (!seekOK(m_diroff)) 
            {
                ErrorExt(this, m_clientdata, module, "%s: Seek error accessing TIFF directory", m_name);
                return 0;
            }
            
            if (!readUInt16OK(dircount)) 
            {
                ErrorExt(this, m_clientdata, module, "%s: Can not read TIFF directory count", m_name);
                return 0;
            }
            
            if ((m_flags & TIFF_SWAB) != 0)
                SwabShort(dircount);

            dir = new TiffDirEntry [dircount];
            if (dir == null)
            {
                ErrorExt(this, m_clientdata, m_name, "No space to read TIFF directory");
                return 0;
            }

            if (!readDirEntryOk(dir, dircount))
            {
                ErrorExt(this, m_clientdata, module, "%.100s: Can not read TIFF directory", m_name);
                delete dir;
                return 0;
            }

            /*
            * Read offset to next directory for sequential scans.
            */
            readUInt32OK(nextdiroff);

            if ((m_flags & TIFF_SWAB) != 0)
                SwabLong(nextdiroff);

            pdir = dir;
            return dircount;
        }

        /*
        * Fetch and set the SubjectDistance EXIF tag.
        */
        private bool fetchSubjectDistance(TiffDirEntry dir)
        {
            bool ok = false;

            byte b[2 * sizeof(uint)];
            int read = fetchData(dir, b);
            if (read != 0)
            {
                uint l[2];
                l[0] = readUInt32(b, 0);
                l[1] = readUInt32(b, sizeof(uint));

                float v;
                if (cvtRational(dir, l[0], l[1], v)) 
                {
                    /*
                    * XXX: Numerator 0xFFFFFFFF means that we have infinite
                    * distance. Indicate that with a negative floating point
                    * SubjectDistance value.
                    */
                    ok = SetField(dir.tdir_tag, (l[0] != 0xFFFFFFFF) ? v : -v);
                }
            }

            return ok;
        }

        /*
        * Check the count field of a directory
        * entry against a known value.  The caller
        * is expected to skip/ignore the tag if
        * there is a mismatch.
        */
        private bool checkDirCount(TiffDirEntry dir, uint count)
        {
            if (count > dir.tdir_count)
            {
                Tiff::WarningExt(this, m_clientdata, m_name, "incorrect count for field \"%s\" (%u, expecting %u); tag ignored", FieldWithTag(dir.tdir_tag).field_name, dir.tdir_count, count);
                return false;
            }
            else if (count < dir.tdir_count)
            {
                Tiff::WarningExt(this, m_clientdata, m_name, "incorrect count for field \"%s\" (%u, expecting %u); tag trimmed", FieldWithTag(dir.tdir_tag).field_name, dir.tdir_count, count);
                return true;
            }

            return true;
        }

        /*
        * Fetch a contiguous directory item.
        */
        private int fetchData(TiffDirEntry dir, byte[] cp)
        {
            /* 
            * FIXME: bytecount should have int type, but for now libtiff
            * defines int as a signed 32-bit integer and we are losing
            * ability to read arrays larger than 2^31 bytes. So we are using
            * uint instead of int here.
            */

            uint w = Tiff::DataWidth((TiffDataType)dir.tdir_type);
            uint cc = dir.tdir_count * w;

            /* Check for overflow. */
            if (dir.tdir_count == 0 || w == 0 || (cc / w) != dir.tdir_count)
                fetchFailed(dir);

            if (!seekOK(dir.tdir_offset))
                fetchFailed(dir);

            if (!readOK(cp, cc))
                fetchFailed(dir);

            if ((m_flags & TIFF_SWAB) != 0)
            {
                switch (dir.tdir_type)
                {
                    case TIFF_SHORT:
                    case TIFF_SSHORT:
                        {
                            UInt16* u = byteArrayToUInt16(cp, 0, cc);
                            Tiff::SwabArrayOfShort(u, dir.tdir_count);
                            uint16ToByteArray(u, 0, dir.tdir_count, cp, 0);
                            delete[] u;
                        }
                        break;
                    case TIFF_LONG:
                    case TIFF_SLONG:
                    case TIFF_FLOAT:
                        {
                            uint* u = byteArrayToUInt(cp, 0, cc);
                            Tiff::SwabArrayOfLong(u, dir.tdir_count);
                            uintToByteArray(u, 0, dir.tdir_count, cp, 0);
                            delete[] u;
                        }
                        break;
                    case TIFF_RATIONAL:
                    case TIFF_SRATIONAL:
                        {
                            uint* u = byteArrayToUInt(cp, 0, cc);
                            Tiff::SwabArrayOfLong(u, 2 * dir.tdir_count);
                            uintToByteArray(u, 0, 2 * dir.tdir_count, cp, 0);
                            delete[] u;
                        }
                        break;
                    case TIFF_DOUBLE:
                        Tiff::swab64BitData(cp, cc);
                        break;
                }
            }

            return cc;
        }

        /*
        * Fetch an ASCII item from the file.
        */
        private int fetchString(TiffDirEntry dir, out string cp)
        {
            if (dir.tdir_count <= 4)
            {
                uint l = dir.tdir_offset;
                if ((m_flags & TIFF_SWAB) != 0)
                    Tiff::SwabLong(l);

                byte bytes[sizeof(uint)];
                writeUInt32(l, bytes, 0);
                memcpy(cp, bytes, dir.tdir_count);
                return 1;
            }

            return fetchData(dir, (byte*)cp);
        }

        /*
        * Convert numerator+denominator to float.
        */
        private bool cvtRational(TiffDirEntry dir, uint num, uint denom, out float rv)
        {
            if (denom == 0)
            {
                Tiff::ErrorExt(this, m_clientdata, m_name, "%s: Rational with zero denominator (num = %u)", FieldWithTag(dir.tdir_tag).field_name, num);
                return false;
            }
            else
            {
                if (dir.tdir_type == TIFF_RATIONAL)
                    rv = ((float)num / (float)denom);
                else
                    rv = ((float)(int)num / (float)(int)denom);

                return true;
            }
        }

        /*
        * Fetch a rational item from the file
        * at offset off and return the value
        * as a floating point number.
        */
        private float fetchRational(TiffDirEntry dir)
        {
            byte bytes[sizeof(uint) * 2];
            int read = fetchData(dir, bytes);
            if (read != 0)
            {
                uint l[2];
                l[0] = readUInt32(bytes, 0);
                l[1] = readUInt32(bytes, sizeof(uint));

                float v;
                bool res = cvtRational(dir, l[0], l[1], v);
                if (res)
                    return v;
            }

            return 1.0f;
        }

        /*
        * Fetch a single floating point value
        * from the offset field and return it
        * as a native float.
        */
        private float fetchFloat(TiffDirEntry dir)
        {
            int l = extractData(dir);

            float v;
            memcpy(&v, &l, sizeof(float));
            return v;
        }

        /*
        * Fetch an array of BYTE or SBYTE values.
        */
        private bool fetchByteArray(TiffDirEntry dir, byte[] v)
        {
            if (dir.tdir_count <= 4)
            {
                /*
                 * Extract data from offset field.
                 */
                uint count = dir.tdir_count;

                if (m_header.tiff_magic == TIFF_BIGENDIAN)
                {
                    if (count == 4)
                        v[3] = dir.tdir_offset & 0xff;

                    if (count >= 3)
                        v[2] = (dir.tdir_offset >> 8) & 0xff;

                    if (count >= 2)
                        v[1] = (dir.tdir_offset >> 16) & 0xff;

                    if (count >= 1)
                        v[0] = dir.tdir_offset >> 24;
                }
                else
                {
                    if (count == 4)
                        v[3] = dir.tdir_offset >> 24;

                    if (count >= 3)
                        v[2] = (dir.tdir_offset >> 16) & 0xff;

                    if (count >= 2)
                        v[1] = (dir.tdir_offset >> 8) & 0xff;

                    if (count >= 1)
                        v[0] = dir.tdir_offset & 0xff;
                }

                return true;
            }

            return (fetchData(dir, v) != 0);
        }

        /*
        * Fetch an array of SHORT or SSHORT values.
        */
        private bool fetchShortArray(TiffDirEntry dir, UInt16[] v)
        {
            if (dir.tdir_count <= 2)
            {
                uint count = dir.tdir_count;

                if (m_header.tiff_magic == TIFF_BIGENDIAN)
                {
                    if (count == 2)
                        v[1] = (UInt16)(dir.tdir_offset & 0xffff);

                    if (count >= 1)
                        v[0] = (UInt16)(dir.tdir_offset >> 16);
                }
                else
                {
                    if (count == 2)
                        v[1] = (UInt16)(dir.tdir_offset >> 16);

                    if (count >= 1)
                        v[0] = (UInt16)(dir.tdir_offset & 0xffff);
                }

                return true;
            }

            uint cc = dir.tdir_count * sizeof(UInt16);
            byte* b = new byte[cc];
            int read = fetchData(dir, b);
            if (read != 0)
            {
                UInt16* u = byteArrayToUInt16(b, 0, read);
                memcpy(v, u, read);
                delete[] u;
            }

            return (read != 0);
        }

        /*
        * Fetch a pair of SHORT or BYTE values. Some tags may have either BYTE
        * or SHORT type and this function works with both ones.
        */
        private bool fetchShortPair(TiffDirEntry dir)
        {
            /*
            * Prevent overflowing the v stack arrays below by performing a sanity
            * check on tdir_count, this should never be greater than two.
            */
            if (dir.tdir_count > 2) 
            {
                WarningExt(this, m_clientdata, m_name, "unexpected count for field \"%s\", %u, expected 2; ignored", FieldWithTag(dir.tdir_tag).field_name, dir.tdir_count);
                return false;
            }

            switch (dir.tdir_type)
            {
                case TIFF_BYTE:
                case TIFF_SBYTE:
                    {
                        byte v[4];
                        return fetchByteArray(dir, v) && SetField(dir.tdir_tag, v[0], v[1]);
                    }
                case TIFF_SHORT:
                case TIFF_SSHORT:
                    {
                        UInt16 v[2];
                        return fetchShortArray(dir, v) && SetField(dir.tdir_tag, v[0], v[1]);
                    }
            }

            return false;
        }

        /*
        * Fetch an array of LONG or SLONG values.
        */
        private bool fetchLongArray(TiffDirEntry dir, uint[] v)
        {
            if (dir.tdir_count == 1)
            {
                v[0] = dir.tdir_offset;
                return true;
            }

            uint cc = dir.tdir_count * sizeof(uint);
            byte* b = new byte[cc];
            int read = fetchData(dir, b);
            if (read != 0)
            {
                uint* u = byteArrayToUInt(b, 0, read);
                memcpy(v, u, read);
                delete[] u;
            }

            return (read != 0);
        }

        /*
        * Fetch an array of RATIONAL or SRATIONAL values.
        */
        private bool fetchRationalArray(TiffDirEntry dir, float[] v)
        {
            assert(sizeof(float) == sizeof(uint));

            bool ok = false;
            byte* l = new byte [dir.tdir_count * Tiff::DataWidth((TiffDataType)dir.tdir_type)];
            if (l == null)
                ErrorExt(this, m_clientdata, m_name, "No space to fetch array of rationals");

            if (l != null)
            {
                if (fetchData(dir, l))
                {
                    int offset = 0;
                    uint pair[2];
                    for (uint i = 0; i < dir.tdir_count; i++)
                    {
                        pair[0] = readUInt32(l, offset);
                        offset += sizeof(uint);
                        pair[1] = readUInt32(l, offset);
                        offset += sizeof(uint);

                        ok = cvtRational(dir, pair[0], pair[1], v[i]);
                        if (!ok)
                            break;
                    }
                }

                delete l;
            }

            return ok;
        }

        /*
        * Fetch an array of FLOAT values.
        */
        private bool fetchFloatArray(TiffDirEntry dir, float[] v)
        {
            if (dir.tdir_count == 1)
            {
                v[0] = *(float*) &dir.tdir_offset;
                return true;
            }

            uint w = Tiff::DataWidth((TiffDataType)dir.tdir_type);
            uint cc = dir.tdir_count * w;
            byte* b = new byte [cc];
            int read = fetchData(dir, b);
            if (read != 0)
                memcpy(v, b, read);

            return (read != 0);
        }

        /*
        * Fetch an array of DOUBLE values.
        */
        private bool fetchDoubleArray(TiffDirEntry dir, double[] v)
        {
            uint w = Tiff::DataWidth((TiffDataType)dir.tdir_type);
            uint cc = dir.tdir_count * w;
            byte* b = new byte [cc];
            int read = fetchData(dir, b);
            if (read != 0)
                memcpy(v, b, read);

            return (read != 0);
        }

        /*
        * Fetch an array of ANY values.  The actual values are
        * returned as doubles which should be able hold all the
        * types.  Yes, there really should be an tany_t to avoid
        * this potential non-portability ...  Note in particular
        * that we assume that the double return value vector is
        * large enough to read in any fundamental type.  We use
        * that vector as a buffer to read in the base type vector
        * and then convert it in place to double (from end
        * to front of course).
        */
        private bool fetchAnyArray(TiffDirEntry dir, double[] v)
        {
            switch (dir.tdir_type)
            {
                case TIFF_BYTE:
                case TIFF_SBYTE:
                    {
                        byte* b = new byte[dir.tdir_count];
                        bool res = fetchByteArray(dir, b);
                        if (res)
                        {
                            for (int i = dir.tdir_count - 1; i >= 0; i--)
                                v[i] = b[i];
                        }
                        delete[] b;
                        if (!res)
                            return false;
                    }
                    break;
                case TIFF_SHORT:
                case TIFF_SSHORT:
                    {
                        UInt16* u = new UInt16[dir.tdir_count];
                        bool res = fetchShortArray(dir, u);
                        if (res)
                        {
                            for (int i = dir.tdir_count - 1; i >= 0; i--)
                                v[i] = u[i];
                        }

                        delete[] u;
                        if (!res)
                            return false;
                    }
                    break;
                case TIFF_LONG:
                case TIFF_SLONG:
                    {
                        uint* l = new uint[dir.tdir_count];
                        bool res = fetchLongArray(dir, l);
                        if (res)
                        {
                            for (int i = dir.tdir_count - 1; i >= 0; i--)
                                v[i] = l[i];
                        }

                        delete[] l;
                        if (!res)
                            return false;
                    }
                    break;
                case TIFF_RATIONAL:
                case TIFF_SRATIONAL:
                    {
                        float* f = new float[dir.tdir_count];
                        bool res = fetchRationalArray(dir, f);
                        if (res)
                        {
                            for (int i = dir.tdir_count - 1; i >= 0; i--)
                                v[i] = f[i];
                        }

                        delete[] f;
                        if (!res)
                            return false;
                    }
                    break;
                case TIFF_FLOAT:
                    {
                        float* f = new float[dir.tdir_count];
                        bool res = fetchFloatArray(dir, f);
                        if (res)
                        {
                            for (int i = dir.tdir_count - 1; i >= 0; i--)
                                v[i] = f[i];
                        }

                        delete[] f;
                        if (!res)
                            return false;
                    }
                    break;
                case TIFF_DOUBLE:
                    return fetchDoubleArray(dir, v);
                default:
                    /* TIFF_NOTYPE */
                    /* TIFF_ASCII */
                    /* TIFF_UNDEFINED */
                    Tiff::ErrorExt(this, m_clientdata, m_name, "cannot read TIFF_ANY type %d for field \"%s\"", dir.tdir_type, FieldWithTag(dir.tdir_tag).field_name);
                    return false;
            }

            return true;
        }

        /*
        * Fetch a tag that is not handled by special case code.
        */
        private bool fetchNormalTag(TiffDirEntry dir)
        {
            static const char mesg[] = "to fetch tag value";
            bool ok = false;
            const TiffFieldInfo* fip = FieldWithTag(dir.tdir_tag);

            if (dir.tdir_count > 1)
            {
                switch (dir.tdir_type)
                {
                    case TIFF_BYTE:
                    case TIFF_SBYTE:
                        {
                            byte* cp = new byte [dir.tdir_count];
                            if (cp == null)
                                ErrorExt(this, m_clientdata, m_name, "No space %s", mesg);

                            ok = (cp != null) && fetchByteArray(dir, cp);
                            if (ok)
                            {
                                if (fip.field_passcount)
                                    ok = SetField(dir.tdir_tag, dir.tdir_count, cp);
                                else
                                    ok = SetField(dir.tdir_tag, cp);
                            }

                            delete cp;
                        }
                        break;
                    case TIFF_SHORT:
                    case TIFF_SSHORT:
                        {
                            UInt16* cp = new UInt16 [dir.tdir_count];
                            if (cp == null)
                                ErrorExt(this, m_clientdata, m_name, "No space %s", mesg);

                            ok = (cp != null) && fetchShortArray(dir, cp);
                            if (ok)
                            {
                                if (fip.field_passcount)
                                    ok = SetField(dir.tdir_tag, dir.tdir_count, cp);
                                else
                                    ok = SetField(dir.tdir_tag, cp);
                            }
                            
                            delete cp;
                        }
                        break;
                    case TIFF_LONG:
                    case TIFF_SLONG:
                        {
                            uint* cp = new uint [dir.tdir_count];
                            if (cp == null)
                                ErrorExt(this, m_clientdata, m_name, "No space %s", mesg);

                            ok = (cp != null) && fetchLongArray(dir, cp);
                            if (ok)
                            {
                                if (fip.field_passcount)
                                    ok = SetField(dir.tdir_tag, dir.tdir_count, cp);
                                else
                                    ok = SetField(dir.tdir_tag, cp);
                            }
                            
                            delete cp;
                        }
                        break;
                    case TIFF_RATIONAL:
                    case TIFF_SRATIONAL:
                        {
                            float* cp = new float [dir.tdir_count];
                            if (cp == null)
                                ErrorExt(this, m_clientdata, m_name, "No space %s", mesg);

                            ok = (cp != null) && fetchRationalArray(dir, cp);
                            if (ok)
                            {
                                if (fip.field_passcount)
                                    ok = SetField(dir.tdir_tag, dir.tdir_count, cp);
                                else
                                    ok = SetField(dir.tdir_tag, cp);
                            }
                            
                            delete cp;
                        }
                        break;
                    case TIFF_FLOAT:
                        {
                            float* cp = new float [dir.tdir_count];
                            if (cp == null)
                                ErrorExt(this, m_clientdata, m_name, "No space %s", mesg);

                            ok = (cp != null) && fetchFloatArray(dir, cp);
                            if (ok)
                            {
                                if (fip.field_passcount)
                                    ok = SetField(dir.tdir_tag, dir.tdir_count, cp);
                                else
                                    ok = SetField(dir.tdir_tag, cp);
                            }
                            
                            delete cp;
                        }
                        break;
                    case TIFF_DOUBLE:
                        {
                            double* cp = new double [dir.tdir_count];
                            if (cp == null)
                                ErrorExt(this, m_clientdata, m_name, "No space %s", mesg);

                            ok = (cp != null) && fetchDoubleArray(dir, cp);
                            if (ok)
                            {
                                if (fip.field_passcount)
                                    ok = SetField(dir.tdir_tag, dir.tdir_count, cp);
                                else
                                    ok = SetField(dir.tdir_tag, cp);
                            }
                            
                            delete cp;
                        }
                        break;
                    case TIFF_ASCII:
                    case TIFF_UNDEFINED:
                        {
                            /* bit of a cheat... */
                            /*
                             * Some vendors write strings w/o the trailing
                             * null byte, so always append one just in case.
                             */
                            char* cp = new char [dir.tdir_count + 1];
                            if (cp == null)
                                ErrorExt(this, m_clientdata, m_name, "No space %s", mesg);

                            ok = (cp != null) && fetchString(dir, cp);
                            if (ok != false)
                                cp[dir.tdir_count] = '\0';

                            if (ok)
                            {
                                if (fip.field_passcount)
                                    ok = SetField(dir.tdir_tag, dir.tdir_count, cp);
                                else
                                    ok = SetField(dir.tdir_tag, cp);
                            }
                             
                            /* XXX */
                            delete cp;
                        }
                        break;
                }        
            }
            else if (checkDirCount(dir, 1))
            {
                /* singleton value */
                switch (dir.tdir_type)
                {
                    case TIFF_BYTE:
                    case TIFF_SBYTE:
                    case TIFF_SHORT:
                    case TIFF_SSHORT:
                        /*
                         * If the tag is also acceptable as a LONG or SLONG
                         * then SetField will expect an uint parameter
                         * passed to it (through varargs).  Thus, for machines
                         * where sizeof (int) != sizeof (uint) we must do
                         * a careful check here.  It's hard to say if this
                         * is worth optimizing.
                         *
                         * NB: We use FieldWithTag here knowing that
                         *     it returns us the first entry in the table
                         *     for the tag and that that entry is for the
                         *     widest potential data type the tag may have.
                         */
                        {
                            TiffDataType type = fip.field_type;
                            if (type != TIFF_LONG && type != TIFF_SLONG)
                            {
                                UInt16 v = (UInt16)extractData(dir);
                                if (fip.field_passcount)
                                {
                                    UInt16 a[1];
                                    a[0] = v;
                                    ok = SetField(dir.tdir_tag, 1, a);
                                }
                                else
                                    ok = SetField(dir.tdir_tag, v);

                                break;
                            }

                            uint v32 = extractData(dir);
                            if (fip.field_passcount)
                            {
                                uint a[1];
                                a[0] = v32;
                                ok = SetField(dir.tdir_tag, 1, a);
                            }
                            else
                                ok = SetField(dir.tdir_tag, v32);
                        }
                        break;

                    case TIFF_LONG:
                    case TIFF_SLONG:
                        {
                            uint v32 = extractData(dir);
                            if (fip.field_passcount)
                            {
                                uint a[1];
                                a[0] = v32;
                                ok = SetField(dir.tdir_tag, 1, a);
                            }
                            else
                                ok = SetField(dir.tdir_tag, v32);
                        }
                        break;
                    case TIFF_RATIONAL:
                    case TIFF_SRATIONAL:
                    case TIFF_FLOAT:
                        {
                            float v = (dir.tdir_type == TIFF_FLOAT ? fetchFloat(dir): fetchRational(dir));
                            if (fip.field_passcount)
                            {
                                float a[1];
                                a[0] = v;
                                ok = SetField(dir.tdir_tag, 1, a);
                            }
                            else
                                ok = SetField(dir.tdir_tag, v);
                        }
                        break;
                    case TIFF_DOUBLE:
                        {
                            double v[1];
                            ok = fetchDoubleArray(dir, v);
                            if (ok)
                            {
                                if (fip.field_passcount)
                                    ok = SetField(dir.tdir_tag, 1, v);
                                else
                                    ok = SetField(dir.tdir_tag, v[0]);
                            }
                        }
                        break;
                    case TIFF_ASCII:
                    case TIFF_UNDEFINED:
                         /* bit of a cheat... */
                        {
                            char c[2];
                            ok = fetchString(dir, c) != 0;
                            if (ok)
                            {
                                c[1] = '\0'; /* XXX paranoid */
                                if (fip.field_passcount)
                                    ok = SetField(dir.tdir_tag, 1, c);
                                else
                                    ok = SetField(dir.tdir_tag, c);
                            }
                        }
                        break;
                }
            }

            return ok;
        }

        /*
        * Fetch samples/pixel short values for 
        * the specified tag and verify that
        * all values are the same.
        */
        private bool fetchPerSampleShorts(TiffDirEntry dir, out UInt16 pl)
        {
            UInt16 samples = m_dir.td_samplesperpixel;
            bool status = false;

            if (checkDirCount(dir, samples))
            {
                UInt16* v = new UInt16 [dir.tdir_count];
                if (v == null)
                    ErrorExt(this, m_clientdata, m_name, "No space to fetch per-sample values");

                if (v != null && fetchShortArray(dir, v))
                {
                    int check_count = dir.tdir_count;
                    if (samples < check_count)
                        check_count = samples;

                    bool failed = false;
                    for (UInt16 i = 1; i < check_count; i++)
                    {
                        if (v[i] != v[0])
                        {
                            Tiff::ErrorExt(this, m_clientdata, m_name, "Cannot handle different per-sample values for field \"%s\"", FieldWithTag(dir.tdir_tag).field_name);
                            failed = true;
                            break;
                        }
                    }

                    if (!failed)
                    {
                        pl = v[0];
                        status = true;
                    }
                }

                delete v;
            }

            return status;
        }

        /*
        * Fetch samples/pixel long values for 
        * the specified tag and verify that
        * all values are the same.
        */
        private bool fetchPerSampleLongs(TiffDirEntry dir, out uint pl)
        {
            UInt16 samples = m_dir.td_samplesperpixel;
            bool status = false;

            if (checkDirCount(dir, samples))
            {
                uint* v = new uint [dir.tdir_count];
                if (v == null)
                    ErrorExt(this, m_clientdata, m_name, "No space to fetch per-sample values");

                if (v != null && fetchLongArray(dir, v))
                {
                    int check_count = dir.tdir_count;
                    if (samples < check_count)
                        check_count = samples;

                    bool failed = false;
                    for (UInt16 i = 1; i < check_count; i++)
                    {
                        if (v[i] != v[0])
                        {
                            Tiff::ErrorExt(this, m_clientdata, m_name, "Cannot handle different per-sample values for field \"%s\"", FieldWithTag(dir.tdir_tag).field_name);
                            failed = true;
                            break;
                        }
                    }

                    if (!failed)
                    {
                        pl = v[0];
                        status = true;
                    }
                }

                delete v;
            }

            return status;
        }

        /*
        * Fetch samples/pixel ANY values for the specified tag and verify that all
        * values are the same.
        */
        private bool fetchPerSampleAnys(TiffDirEntry dir, out double pl)
        {
            UInt16 samples = m_dir.td_samplesperpixel;
            bool status = false;

            if (checkDirCount(dir, samples))
            {
                double* v = new double [dir.tdir_count];
                if (v == null)
                    ErrorExt(this, m_clientdata, m_name, "No space to fetch per-sample values");

                if (v != null && fetchAnyArray(dir, v))
                {
                    int check_count = dir.tdir_count;
                    if (samples < check_count)
                        check_count = samples;

                    bool failed = false;
                    for (UInt16 i = 1; i < check_count; i++)
                    {
                        if (v[i] != v[0])
                        {
                            Tiff::ErrorExt(this, m_clientdata, m_name, "Cannot handle different per-sample values for field \"%s\"", FieldWithTag(dir.tdir_tag).field_name);
                            failed = true;
                            break;
                        }
                    }

                    if (!failed)
                    {
                        pl = v[0];
                        status = true;
                    }
                }

                delete v;
            }

            return status;
        }

        /*
        * Fetch a set of offsets or lengths.
        * While this routine says "strips", in fact it's also used for tiles.
        */
        private bool fetchStripThing(TiffDirEntry dir, int nstrips, ref uint[] lpp)
        {
            checkDirCount(dir, nstrips);

            /*
             * Allocate space for strip information.
             */
            if (lpp == null)
            {
                lpp = new uint[nstrips];
                if (lpp == null)
                {
                    ErrorExt(this, m_clientdata, m_name, "No space for strip array");
                    return false;
                }
            }

            memset(lpp, 0, sizeof(uint) * nstrips);

            bool status = false;
            if (dir.tdir_type == (int)TIFF_SHORT)
            {
                /*
                 * Handle uint16->uint expansion.
                 */
                UInt16* dp = new UInt16[dir.tdir_count];
                if (dp == null)
                {
                    ErrorExt(this, m_clientdata, m_name, "No space to fetch strip tag");
                    return false;
                }

                status = fetchShortArray(dir, dp);
                if (status)
                {
                    for (int i = 0; i < nstrips && i < (int)dir.tdir_count; i++)
                        lpp[i] = dp[i];
                }

                delete dp;
            }
            else if (nstrips != (int)dir.tdir_count)
            {
                /* Special case to correct length */

                uint* dp = new uint[dir.tdir_count];
                if (dp == null)
                {
                    ErrorExt(this, m_clientdata, m_name, "No space to fetch strip tag");
                    return false;
                }

                status = fetchLongArray(dir, dp);
                if (status)
                {
                    for (int i = 0; i < nstrips && i < (int)dir.tdir_count; i++)
                        lpp[i] = dp[i];
                }

                delete dp;
            }
            else
                status = fetchLongArray(dir, lpp);

            return status;
        }

        /*
        * Fetch and set the RefBlackWhite tag.
        */
        private bool fetchRefBlackWhite(TiffDirEntry dir)
        {
            static const char mesg[] = "for \"ReferenceBlackWhite\" array";

            if (dir.tdir_type == TIFF_RATIONAL)
                return fetchNormalTag(dir);
            
            /*
             * Handle LONG's for backward compatibility.
             */
            uint* cp = new uint [dir.tdir_count];
            if (cp == null)
                ErrorExt(this, m_clientdata, m_name, "No space %s", mesg);

            bool ok = (cp != null) && fetchLongArray(dir, cp);
            if (ok)
            {
                float* fp = new float [dir.tdir_count];
                if (fp == null)
                {
                    delete cp;
                    cp = null;
                    ErrorExt(this, m_clientdata, m_name, "No space %s", mesg);
                }

                ok = (fp != null);
                if (ok)
                {
                    for (uint i = 0; i < dir.tdir_count; i++)
                        fp[i] = (float)cp[i];

                    ok = SetField(dir.tdir_tag, fp);
                    delete fp;
                }
            }

            delete cp;
            return ok;
        }

        /*
        * Replace a single strip (tile) of uncompressed data by
        * multiple strips (tiles), each approximately 8Kbytes.
        * This is useful for dealing with large images or
        * for dealing with machines with a limited amount
        * memory.
        */
        private void chopUpSingleUncompressedStrip()
        {
            uint bytecount = m_dir.td_stripbytecount[0];
            uint offset = m_dir.td_stripoffset[0];

            /*
             * Make the rows hold at least one scanline, but fill specified amount
             * of data if possible.
             */
            int rowbytes = VTileSize(1);
            int stripbytes;
            uint rowsperstrip;
            if (rowbytes > STRIP_SIZE_DEFAULT)
            {
                stripbytes = rowbytes;
                rowsperstrip = 1;
            }
            else if (rowbytes > 0)
            {
                rowsperstrip = STRIP_SIZE_DEFAULT / rowbytes;
                stripbytes = rowbytes * rowsperstrip;
            }
            else
                return ;

            /* 
             * never increase the number of strips in an image
             */
            if (rowsperstrip >= m_dir.td_rowsperstrip)
                return ;
            
            uint nstrips = Tiff::howMany(bytecount, stripbytes);
            if (nstrips == 0)
            {
                /* something is wonky, do nothing. */
                return ;
            }

            uint* newcounts = new uint [nstrips];
            if (newcounts == null)
                ErrorExt(this, m_clientdata, m_name, "No space for chopped \"StripByteCounts\" array");

            uint* newoffsets = new uint [nstrips];
            if (newoffsets == null)
                ErrorExt(this, m_clientdata, m_name, "No space for chopped \"StripOffsets\" array");

            if (newcounts == null || newoffsets == null)
            {
                /*
                 * Unable to allocate new strip information, give
                 * up and use the original one strip information.
                 */
                delete newcounts;
                delete newoffsets;
                return ;
            }
            
            /*
             * Fill the strip information arrays with new bytecounts and offsets
             * that reflect the broken-up format.
             */
            for (uint strip = 0; strip < nstrips; strip++)
            {
                if ((uint)stripbytes > bytecount)
                    stripbytes = bytecount;

                newcounts[strip] = stripbytes;
                newoffsets[strip] = offset;
                offset += stripbytes;
                bytecount -= stripbytes;
            }

            /*
             * Replace old single strip info with multi-strip info.
             */
            m_dir.td_nstrips = nstrips;
            m_dir.td_stripsperimage = nstrips;
            SetField(TIFFTAG_ROWSPERSTRIP, rowsperstrip);

            delete m_dir.td_stripbytecount;
            delete m_dir.td_stripoffset;
            m_dir.td_stripbytecount = newcounts;
            m_dir.td_stripoffset = newoffsets;
            m_dir.td_stripbytecountsorted = 1;
        }

        internal static uint roundUp(uint x, uint y)
        {
            return (howMany(x, y) * y);
        }

        internal static uint howMany(uint x, uint y)
        {
            return ((x + (y - 1)) / y);
        }
    }
}
