//go:build !darwin

package runtime

import (
	"errors"
	"time"

	"github.com/jandedobbeleer/oh-my-posh/src/cache"
	"github.com/jandedobbeleer/oh-my-posh/src/log"
	"github.com/jandedobbeleer/oh-my-posh/src/runtime/battery"
)

// noBatteryCacheKey marks, in the device cache, a machine without a battery: in-process, enumerating
// battery devices to find none is a noticeable part of every prompt on a desktop.
const noBatteryCacheKey = "battery_none"

func (term *Terminal) BatteryState() (*battery.Info, error) {
	defer log.Trace(time.Now())
	if InProcess {
		if _, ok := term.deviceCache.Get(noBatteryCacheKey); ok {
			return nil, &battery.NoBatteryError{}
		}
	}

	info, err := battery.Get()
	if err != nil {
		var noBattery *battery.NoBatteryError
		if InProcess && errors.As(err, &noBattery) {
			term.deviceCache.Set(noBatteryCacheKey, "true", cache.Duration("1h"))
		}

		log.Error(err)
		return nil, err
	}
	return info, nil
}
