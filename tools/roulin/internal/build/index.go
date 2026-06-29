package build

import (
	"bytes"
	"sort"

	flatbuffers "github.com/google/flatbuffers/go"

	fbs "github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/parcel/generated/roulin_fbs"
)

// BuildIndexBytes serialises IndexEntry values into a FlatBuffers Index
// buffer. Entries are sorted by BlobHash (memcmp) so roulin-core can
// binary-search for "the metadata of blob X". types is the intern table
// referenced by Address.TypeIdxs.
func BuildIndexBytes(entries []IndexEntry, types []string) []byte {
	sorted := make([]IndexEntry, len(entries))
	copy(sorted, entries)
	sort.Slice(sorted, func(i, j int) bool {
		return bytes.Compare(sorted[i].BlobHash[:], sorted[j].BlobHash[:]) < 0
	})

	b := flatbuffers.NewBuilder((len(entries) + 1) * 256)

	entryOffsets := make([]flatbuffers.UOffsetT, len(sorted))
	for i := len(sorted) - 1; i >= 0; i-- {
		entryOffsets[i] = writeIndexEntry(b, sorted[i])
	}

	fbs.IndexStartEntriesVector(b, len(entryOffsets))
	for i := len(entryOffsets) - 1; i >= 0; i-- {
		b.PrependUOffsetT(entryOffsets[i])
	}
	entriesVec := b.EndVector(len(entryOffsets))

	var typesVec flatbuffers.UOffsetT
	if len(types) > 0 {
		typeOffsets := make([]flatbuffers.UOffsetT, len(types))
		for i := len(types) - 1; i >= 0; i-- {
			typeOffsets[i] = b.CreateString(types[i])
		}
		fbs.IndexStartTypesVector(b, len(typeOffsets))
		for i := len(typeOffsets) - 1; i >= 0; i-- {
			b.PrependUOffsetT(typeOffsets[i])
		}
		typesVec = b.EndVector(len(typeOffsets))
	}

	fbs.IndexStart(b)
	fbs.IndexAddEntries(b, entriesVec)
	if typesVec != 0 {
		fbs.IndexAddTypes(b, typesVec)
	}
	root := fbs.IndexEnd(b)
	fbs.FinishIndexBuffer(b, root)
	return b.FinishedBytes()
}

// writeIndexEntry serialises one IndexEntry, building all nested objects
// (blob_hash vector, deps strings, Address tables) before opening the
// IndexEntry table — FlatBuffers forbids interleaved table construction.
func writeIndexEntry(b *flatbuffers.Builder, e IndexEntry) flatbuffers.UOffsetT {
	hashOff := b.CreateByteVector(e.BlobHash[:])

	var nameOff flatbuffers.UOffsetT
	if e.Name != "" {
		nameOff = b.CreateString(e.Name)
	}

	var depsOff flatbuffers.UOffsetT
	if len(e.Deps) > 0 {
		strOffsets := make([]flatbuffers.UOffsetT, len(e.Deps))
		for i := len(e.Deps) - 1; i >= 0; i-- {
			strOffsets[i] = b.CreateString(e.Deps[i])
		}
		fbs.IndexEntryStartDepsVector(b, len(strOffsets))
		for i := len(strOffsets) - 1; i >= 0; i-- {
			b.PrependUOffsetT(strOffsets[i])
		}
		depsOff = b.EndVector(len(strOffsets))
	}

	var addressesOff flatbuffers.UOffsetT
	if len(e.Addresses) > 0 {
		addrOffsets := make([]flatbuffers.UOffsetT, len(e.Addresses))
		for i := len(e.Addresses) - 1; i >= 0; i-- {
			addrOffsets[i] = writeAddress(b, e.Addresses[i])
		}
		fbs.IndexEntryStartAddressesVector(b, len(addrOffsets))
		for i := len(addrOffsets) - 1; i >= 0; i-- {
			b.PrependUOffsetT(addrOffsets[i])
		}
		addressesOff = b.EndVector(len(addrOffsets))
	}

	fbs.IndexEntryStart(b)
	fbs.IndexEntryAddBlobHash(b, hashOff)
	if e.SizeBytes != 0 {
		fbs.IndexEntryAddSizeBytes(b, e.SizeBytes)
	}
	if depsOff != 0 {
		fbs.IndexEntryAddDeps(b, depsOff)
	}
	if addressesOff != 0 {
		fbs.IndexEntryAddAddresses(b, addressesOff)
	}
	if nameOff != 0 {
		fbs.IndexEntryAddName(b, nameOff)
	}
	return fbs.IndexEntryEnd(b)
}

func writeAddress(b *flatbuffers.Builder, a Address) flatbuffers.UOffsetT {
	addrStrOff := b.CreateString(a.AddressStr)

	var assetIDOff flatbuffers.UOffsetT
	if a.AssetID != "" {
		assetIDOff = b.CreateString(a.AssetID)
	}

	var labelsOff flatbuffers.UOffsetT
	if len(a.Labels) > 0 {
		labelOffsets := make([]flatbuffers.UOffsetT, len(a.Labels))
		for i := len(a.Labels) - 1; i >= 0; i-- {
			labelOffsets[i] = b.CreateString(a.Labels[i])
		}
		fbs.AddressStartLabelsVector(b, len(labelOffsets))
		for i := len(labelOffsets) - 1; i >= 0; i-- {
			b.PrependUOffsetT(labelOffsets[i])
		}
		labelsOff = b.EndVector(len(labelOffsets))
	}

	var typeIdxsOff flatbuffers.UOffsetT
	if len(a.TypeIdxs) > 0 {
		fbs.AddressStartTypeIdxsVector(b, len(a.TypeIdxs))
		for i := len(a.TypeIdxs) - 1; i >= 0; i-- {
			b.PrependUint32(a.TypeIdxs[i])
		}
		typeIdxsOff = b.EndVector(len(a.TypeIdxs))
	}

	fbs.AddressStart(b)
	fbs.AddressAddAddress64(b, fnv1a64(a.AddressStr))
	fbs.AddressAddAddressStr(b, addrStrOff)
	if assetIDOff != 0 {
		fbs.AddressAddAssetId(b, assetIDOff)
	}
	if labelsOff != 0 {
		fbs.AddressAddLabels(b, labelsOff)
	}
	if a.Flags != 0 {
		fbs.AddressAddFlags(b, a.Flags)
	}
	if a.KeyID != 0 {
		fbs.AddressAddKeyId(b, a.KeyID)
	}
	if typeIdxsOff != 0 {
		fbs.AddressAddTypeIdxs(b, typeIdxsOff)
	}
	return fbs.AddressEnd(b)
}

// fnv1a64 reproduces HashAddress() from roulin-core for binary-search
// compatibility on the address64 field stored in each Address.
func fnv1a64(s string) uint64 {
	const (
		offset = uint64(14695981039346656037)
		prime  = uint64(1099511628211)
	)
	h := offset
	for _, c := range []byte(s) {
		h ^= uint64(c)
		h *= prime
	}
	return h
}

// ParseIndexBytes reads a serialised Index FlatBuffer back into IndexEntry
// values. Order matches the stored order (= blob_hash sorted). types is
// the intern table; Address.TypeIdxs reference it.
func ParseIndexBytes(buf []byte) (entries []IndexEntry, types []string) {
	raw := fbs.GetRootAsIndex(buf, 0)

	entries = make([]IndexEntry, 0, raw.EntriesLength())
	var fbEntry fbs.IndexEntry
	for i := 0; i < raw.EntriesLength(); i++ {
		if !raw.Entries(&fbEntry, i) {
			continue
		}
		var h [32]byte
		copy(h[:], fbEntry.BlobHashBytes())

		var deps []string
		if n := fbEntry.DepsLength(); n > 0 {
			deps = make([]string, n)
			for j := 0; j < n; j++ {
				deps[j] = string(fbEntry.Deps(j))
			}
		}

		var addresses []Address
		if n := fbEntry.AddressesLength(); n > 0 {
			addresses = make([]Address, 0, n)
			var fbAddr fbs.Address
			for j := 0; j < n; j++ {
				if !fbEntry.Addresses(&fbAddr, j) {
					continue
				}
				addresses = append(addresses, parseAddress(&fbAddr))
			}
		}

		entries = append(entries, IndexEntry{
			BlobHash:  h,
			SizeBytes: fbEntry.SizeBytes(),
			Deps:      deps,
			Addresses: addresses,
			Name:      string(fbEntry.Name()),
		})
	}

	if n := raw.TypesLength(); n > 0 {
		types = make([]string, n)
		for i := 0; i < n; i++ {
			types[i] = string(raw.Types(i))
		}
	}
	return entries, types
}

func parseAddress(a *fbs.Address) Address {
	var labels []string
	if n := a.LabelsLength(); n > 0 {
		labels = make([]string, n)
		for j := 0; j < n; j++ {
			labels[j] = string(a.Labels(j))
		}
	}
	var typeIdxs []uint32
	if n := a.TypeIdxsLength(); n > 0 {
		typeIdxs = make([]uint32, n)
		for j := 0; j < n; j++ {
			typeIdxs[j] = a.TypeIdxs(j)
		}
	}
	return Address{
		AddressStr: string(a.AddressStr()),
		AssetID:    string(a.AssetId()),
		Labels:     labels,
		Flags:      a.Flags(),
		KeyID:      a.KeyId(),
		TypeIdxs:   typeIdxs,
	}
}
