package main

import (
	"regexp"
	"strings"
)

var semanticVersionPattern = regexp.MustCompile(`^([0-9]+)\.([0-9]+)\.([0-9]+)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$`)

type semanticVersion struct {
	core       [3]string
	preRelease []string
}

func parseSemanticVersion(value string) (semanticVersion, bool) {
	match := semanticVersionPattern.FindStringSubmatch(value)
	if match == nil {
		return semanticVersion{}, false
	}
	parsed := semanticVersion{core: [3]string{match[1], match[2], match[3]}}
	for _, identifier := range parsed.core {
		if len(identifier) > 1 && identifier[0] == '0' {
			return semanticVersion{}, false
		}
	}
	if match[4] == "" {
		return parsed, true
	}
	parsed.preRelease = strings.Split(match[4], ".")
	for _, identifier := range parsed.preRelease {
		if isNumericIdentifier(identifier) && len(identifier) > 1 && identifier[0] == '0' {
			return semanticVersion{}, false
		}
	}
	return parsed, true
}

func compareSemanticVersions(left, right string) (int, bool) {
	leftVersion, leftValid := parseSemanticVersion(left)
	rightVersion, rightValid := parseSemanticVersion(right)
	if !leftValid || !rightValid {
		return 0, false
	}
	for index := range leftVersion.core {
		if comparison := compareNumericIdentifier(leftVersion.core[index], rightVersion.core[index]); comparison != 0 {
			return comparison, true
		}
	}
	if len(leftVersion.preRelease) == 0 && len(rightVersion.preRelease) == 0 {
		return 0, true
	}
	if len(leftVersion.preRelease) == 0 {
		return 1, true
	}
	if len(rightVersion.preRelease) == 0 {
		return -1, true
	}
	count := len(leftVersion.preRelease)
	if len(rightVersion.preRelease) < count {
		count = len(rightVersion.preRelease)
	}
	for index := 0; index < count; index++ {
		leftIdentifier := leftVersion.preRelease[index]
		rightIdentifier := rightVersion.preRelease[index]
		leftNumeric := isNumericIdentifier(leftIdentifier)
		rightNumeric := isNumericIdentifier(rightIdentifier)
		var comparison int
		switch {
		case leftNumeric && rightNumeric:
			comparison = compareNumericIdentifier(leftIdentifier, rightIdentifier)
		case leftNumeric:
			comparison = -1
		case rightNumeric:
			comparison = 1
		case leftIdentifier < rightIdentifier:
			comparison = -1
		case leftIdentifier > rightIdentifier:
			comparison = 1
		}
		if comparison != 0 {
			return comparison, true
		}
	}
	switch {
	case len(leftVersion.preRelease) < len(rightVersion.preRelease):
		return -1, true
	case len(leftVersion.preRelease) > len(rightVersion.preRelease):
		return 1, true
	default:
		return 0, true
	}
}

func compareNumericIdentifier(left, right string) int {
	if len(left) < len(right) {
		return -1
	}
	if len(left) > len(right) {
		return 1
	}
	if left < right {
		return -1
	}
	if left > right {
		return 1
	}
	return 0
}

func isNumericIdentifier(value string) bool {
	if value == "" {
		return false
	}
	for _, character := range value {
		if character < '0' || character > '9' {
			return false
		}
	}
	return true
}
