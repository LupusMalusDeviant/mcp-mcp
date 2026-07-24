(component
  (core module $module
    (func (export "spin")
      (loop $forever
        br $forever)))
  (core instance $instance (instantiate $module))
  (func $spin
    (canon lift (core func $instance "spin")))
  (export "spin" (func $spin)))
